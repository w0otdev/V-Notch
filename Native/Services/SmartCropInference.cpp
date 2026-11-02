#include "SmartCropInference.h"
#include "../vnotch_handle_registry.h"
#define ORT_API_MANUAL_INIT
#include <onnxruntime_cxx_api.h>
#include <array>
#include <memory>
#include <mutex>
#include <stdexcept>

namespace {
using Detection = vnotch::services::VnDetection;
using AppendDml = OrtStatus*(ORT_API_CALL*)(OrtSessionOptions*, int);

// Keep the runtime loaded while any Ort object (including the managed compatibility API) exists.
void InitializeRuntime(const std::wstring& directory) {
    static const bool initialized = [&] {
        HMODULE module = LoadLibraryExW((directory + L"\\onnxruntime.dll").c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) module = LoadLibraryExW((directory + L"\\runtimes\\win-x64\\native\\onnxruntime.dll").c_str(),
            nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) throw std::runtime_error("ONNX Runtime unavailable");
        const auto get_api = reinterpret_cast<const OrtApiBase*(ORT_API_CALL*)()>(GetProcAddress(module, "OrtGetApiBase"));
        const OrtApi* api = get_api ? get_api()->GetApi(ORT_API_VERSION) : nullptr;
        if (!api) { FreeLibrary(module); throw std::runtime_error("Incompatible ONNX Runtime"); }
        Ort::InitApi(api);
        return true;
    }();
    (void)initialized;
}

struct CropSession {
    Ort::Env env{ORT_LOGGING_LEVEL_WARNING, "VNotch.SmartCrop"};
    Ort::Session session{nullptr};
    Ort::MemoryInfo memory = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
    std::vector<float> input = std::vector<float>(3 * 416 * 416);
    std::vector<float> output = std::vector<float>(3549 * 85);
    Ort::Value input_value{nullptr}, output_value{nullptr};
    std::wstring model;
    std::string input_name, output_name;
    bool dml = false;
    std::mutex gate;

    explicit CropSession(std::wstring path, bool prefer_dml) : model(std::move(path)) {
        try { Open(prefer_dml); }
        catch (...) { if (!prefer_dml) throw; Open(false); }
        constexpr std::array<int64_t, 4> input_shape{1, 3, 416, 416};
        constexpr std::array<int64_t, 3> output_shape{1, 3549, 85};
        input_value = Ort::Value::CreateTensor<float>(memory, input.data(), input.size(), input_shape.data(), input_shape.size());
        output_value = Ort::Value::CreateTensor<float>(memory, output.data(), output.size(), output_shape.data(), output_shape.size());
    }

    void Open(bool prefer_dml) {
        Ort::SessionOptions options;
        options.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
        options.SetExecutionMode(ExecutionMode::ORT_SEQUENTIAL);
        options.DisableMemPattern();
        options.DisableCpuMemArena();
        options.SetInterOpNumThreads(1); options.SetIntraOpNumThreads(2);
        if (prefer_dml) {
            const auto append = reinterpret_cast<AppendDml>(GetProcAddress(GetModuleHandleW(L"onnxruntime.dll"),
                "OrtSessionOptionsAppendExecutionProvider_DML"));
            if (!append) throw std::runtime_error("DirectML provider unavailable");
            Ort::ThrowOnError(append(options, 0));
        }
        Ort::Session candidate(env, model.c_str(), options);
        if (candidate.GetInputCount() != 1 || candidate.GetOutputCount() != 1 ||
            candidate.GetInputTypeInfo(0).GetTensorTypeAndShapeInfo().GetShape() != std::vector<int64_t>{1, 3, 416, 416} ||
            candidate.GetOutputTypeInfo(0).GetTensorTypeAndShapeInfo().GetShape() != std::vector<int64_t>{1, 3549, 85} ||
            candidate.GetInputTypeInfo(0).GetTensorTypeAndShapeInfo().GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT ||
            candidate.GetOutputTypeInfo(0).GetTensorTypeAndShapeInfo().GetElementType() != ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT)
            throw std::runtime_error("Unexpected YOLOX model signature");
        Ort::AllocatorWithDefaultOptions allocator;
        input_name = candidate.GetInputNameAllocated(0, allocator).get();
        output_name = candidate.GetOutputNameAllocated(0, allocator).get();
        session = std::move(candidate); dml = prefer_dml;
    }

    void Run() {
        const char* inputs[]{input_name.c_str()}, *outputs[]{output_name.c_str()};
        session.Run(Ort::RunOptions{nullptr}, inputs, &input_value, 1, outputs, &output_value, 1);
    }
};
vnotch::HandleRegistry<CropSession> sessions;
}

VN_EXPORT int32_t vn_smartcrop_parse_yolox_and_nms(const float*, int32_t, int32_t, float,
    Detection*, int32_t, int32_t*, int32_t*) noexcept;

VN_EXPORT int32_t __stdcall vn_smartcrop_session_create(const wchar_t* model, int32_t model_length,
    const wchar_t* directory, int32_t directory_length, uint8_t prefer_dml,
    int64_t* out_handle, uint8_t* out_dml) noexcept {
    if (!model || model_length <= 0 || !directory || directory_length <= 0 || !out_handle || !out_dml)
        return VN_ERR_INVALID_ARG;
    *out_handle = 0; *out_dml = 0;
    try {
        InitializeRuntime(std::wstring(directory, directory_length));
        auto session = std::make_unique<CropSession>(std::wstring(model, model_length), prefer_dml != 0);
        *out_dml = session->dml ? 1 : 0;
        *out_handle = sessions.Add(std::move(session));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_smartcrop_session_destroy(int64_t handle) noexcept {
    try { sessions.Remove(handle); return VN_OK; } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_smartcrop_session_infer(int64_t handle, const uint8_t* pixels, int32_t length,
    int32_t width, int32_t height, int32_t stride, Detection* detections, int32_t capacity,
    int32_t* out_count, uint8_t* out_dml) noexcept {
    if (!out_count || !out_dml || capacity < 3549 || !detections) return VN_ERR_INVALID_ARG;
    *out_count = 0;
    try {
        auto session = sessions.Acquire(handle);
        if (!session) return VN_ERR_INVALID_ARG;
        std::lock_guard lock(session->gate);
        float scale = 0;
        int rc = vn_smartcrop_preprocess_bgra32(pixels, length, width, height, stride,
            session->input.data(), static_cast<int32_t>(session->input.size()), &scale);
        if (rc != VN_OK) return rc;
        try { session->Run(); }
        catch (...) { if (!session->dml) throw; session->Open(false); session->Run(); }
        *out_dml = session->dml ? 1 : 0;
        int32_t raw = 0;
        return vn_smartcrop_parse_yolox_and_nms(session->output.data(), width, height, scale,
            detections, capacity, &raw, out_count);
    } catch (...) { return VN_ERR_FAIL; }
}
