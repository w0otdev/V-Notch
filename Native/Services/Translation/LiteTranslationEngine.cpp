#include "vnotch_native.h"
#include "LiteTranslationAbi.h"
#include <array>
#include <mutex>

namespace {
struct Runtime {
    HMODULE module = nullptr;
    VnLiteOpen open = nullptr;
    VnLiteTranslate translate = nullptr;
    VnLiteClose close = nullptr;
    VnLiteReleaseIdle release_idle = nullptr;
    VnLiteSetNatural set_natural = nullptr;
};
Runtime runtime;
std::mutex runtime_gate;

bool LoadRuntime() noexcept {
    try {
        std::lock_guard lock(runtime_gate);
        if (runtime.module) return true;
        HMODULE owner = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&LoadRuntime), &owner)) return false;
        std::array<wchar_t, 32768> path{};
        DWORD length = GetModuleFileNameW(owner, path.data(), static_cast<DWORD>(path.size()));
        if (!length || length >= path.size()) return false;
        std::wstring folder(path.data(), length);
        size_t slash = folder.find_last_of(L"\\/");
        if (slash == std::wstring::npos) return false;
        folder.resize(slash + 1);
        folder += L"translation\\VNotch.Translation.dll";
        HMODULE module = LoadLibraryExW(folder.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) return false;
        auto open = reinterpret_cast<VnLiteOpen>(GetProcAddress(module, "vn_lite_open"));
        auto translate = reinterpret_cast<VnLiteTranslate>(GetProcAddress(module, "vn_lite_translate"));
        auto close = reinterpret_cast<VnLiteClose>(GetProcAddress(module, "vn_lite_close"));
        auto release_idle = reinterpret_cast<VnLiteReleaseIdle>(GetProcAddress(module, "vn_lite_release_idle"));
        auto set_natural = reinterpret_cast<VnLiteSetNatural>(GetProcAddress(module, "vn_lite_set_natural"));
        if (!open || !translate || !close || !release_idle || !set_natural) { FreeLibrary(module); return false; }
        runtime = {module, open, translate, close, release_idle, set_natural};
        return true;
    } catch (...) { return false; }
}
}

VN_EXPORT int32_t __stdcall vn_translation_lite_open(const wchar_t* root, int32_t length, int32_t threads, int64_t* handle) noexcept {
    if (!handle) return VN_ERR_INVALID_ARG;
    *handle = 0;
    return LoadRuntime() ? runtime.open(root, length, threads, handle) : VN_TRANSLATION_RUNTIME;
}
VN_EXPORT int32_t __stdcall vn_translation_lite_translate(int64_t handle, const wchar_t* text, int32_t length,
    const wchar_t* target, int32_t target_length, VnTranslationCancelled cancel, intptr_t state,
    wchar_t* output, int32_t capacity, int32_t* written) noexcept {
    if (!written) return VN_ERR_INVALID_ARG;
    *written = 0;
    return LoadRuntime() ? runtime.translate(handle, text, length, target, target_length, cancel, state, output, capacity, written) : VN_TRANSLATION_RUNTIME;
}
VN_EXPORT int32_t __stdcall vn_translation_lite_close(int64_t handle) noexcept {
    return LoadRuntime() ? runtime.close(handle) : VN_TRANSLATION_RUNTIME;
}
VN_EXPORT int32_t __stdcall vn_translation_lite_set_natural(int64_t handle, uint8_t enabled) noexcept {
    return LoadRuntime() ? runtime.set_natural(handle, enabled) : VN_TRANSLATION_RUNTIME;
}
VN_EXPORT int32_t __stdcall vn_translation_lite_release_idle(int64_t handle, int32_t idle_minutes, uint8_t* released) noexcept {
    if (!released || idle_minutes < 0) return VN_ERR_INVALID_ARG;
    *released = 0;
    return LoadRuntime() ? runtime.release_idle(handle, static_cast<int64_t>(idle_minutes) * 60000, released) : VN_TRANSLATION_RUNTIME;
}
