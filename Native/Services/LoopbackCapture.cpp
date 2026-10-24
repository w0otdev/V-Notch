#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <ks.h>
#include <ksmedia.h>
#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")

// WASAPI loopback capture of a render endpoint for the music visualizer. A worker thread drains packets
// and hands the raw mix-format bytes to a callback; the managed side does the analysis.

namespace vnotch::services::loopback {

using vnotch::com::ComPtr;
using vnotch::com::EnsureMta;
using vnotch::com::Guard;

using DataCallback = void(__stdcall*)(int64_t context, const uint8_t* data, int32_t bytes);
using StoppedCallback = void(__stdcall*)(int64_t context, int32_t hresult);

constexpr REFERENCE_TIME kBufferDuration = 1'000'000; // 100 ms
constexpr DWORD kPollMs = 10;
constexpr DWORD kJoinMs = 2000;

#pragma pack(push, 1)
struct VnLoopbackFormat {
    int32_t sample_rate;
    int32_t channels;
    int32_t bits_per_sample;
    uint8_t is_float;
};
#pragma pack(pop)

struct Capture {
    ComPtr<IMMDevice> device;
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    std::wstring device_id;
    VnLoopbackFormat format{};
    int32_t block_align = 0;
    HANDLE thread = nullptr;
    HANDLE stop_event = nullptr;
    DataCallback on_data = nullptr;
    StoppedCallback on_stopped = nullptr;
    int64_t context = 0;
    std::atomic<bool> stopping{false};

    Capture() noexcept { stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr); }
    Capture(const Capture&) = delete;
    Capture& operator=(const Capture&) = delete;
    ~Capture() {
        Stop();
        if (stop_event) CloseHandle(stop_event);
    }

    void Stop() noexcept {
        stopping = true;
        if (stop_event) SetEvent(stop_event);
        if (thread) {
            WaitForSingleObject(thread, kJoinMs);
            CloseHandle(thread);
            thread = nullptr;
        }
        if (client.ptr) client.ptr->Stop();
    }

    void Run() noexcept {
        EnsureMta();
        std::vector<uint8_t> silence;
        HRESULT failure = S_OK;
        while (WaitForSingleObject(stop_event, kPollMs) == WAIT_TIMEOUT) {
            UINT32 packet = 0;
            HRESULT hr = capture.ptr->GetNextPacketSize(&packet);
            while (SUCCEEDED(hr) && packet > 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = capture.ptr->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (FAILED(hr)) break;
                const int32_t bytes = static_cast<int32_t>(frames) * block_align;
                if (!stopping.load() && on_data) {
                    if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
                        if (silence.size() < static_cast<size_t>(bytes)) silence.assign(static_cast<size_t>(bytes), 0);
                        on_data(context, silence.data(), bytes);
                    } else {
                        on_data(context, data, bytes);
                    }
                }
                capture.ptr->ReleaseBuffer(frames);
                hr = capture.ptr->GetNextPacketSize(&packet);
            }
            if (FAILED(hr)) {
                failure = hr;
                break;
            }
        }
        if (FAILED(failure) && !stopping.load() && on_stopped) on_stopped(context, static_cast<int32_t>(failure));
    }

    static DWORD WINAPI Main(LPVOID param) noexcept {
        static_cast<Capture*>(param)->Run();
        return 0;
    }
};

SRWLOCK g_lock = SRWLOCK_INIT;
std::vector<std::unique_ptr<Capture>> g_captures;

Capture* Find(int64_t handle) noexcept {
    Guard guard(&g_lock);
    if (handle <= 0 || static_cast<size_t>(handle) > g_captures.size()) return nullptr;
    return g_captures[static_cast<size_t>(handle) - 1].get();
}

// Selected endpoint when it is active, otherwise the default render endpoint.
HRESULT ResolveDevice(IMMDeviceEnumerator* enumerator, const std::wstring& requested, ComPtr<IMMDevice>& out) {
    if (!requested.empty()) {
        ComPtr<IMMDevice> selected;
        DWORD state = 0;
        if (SUCCEEDED(enumerator->GetDevice(requested.c_str(), selected.Put())) && selected.ptr &&
            SUCCEEDED(selected.ptr->GetState(&state)) && state == DEVICE_STATE_ACTIVE) {
            out.ptr = selected.ptr;
            selected.ptr = nullptr;
            return S_OK;
        }
    }
    return enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, out.Put());
}

std::wstring DeviceId(IMMDevice* device) {
    LPWSTR id = nullptr;
    std::wstring result;
    if (device && SUCCEEDED(device->GetId(&id)) && id) {
        result = id;
        CoTaskMemFree(id);
    }
    return result;
}

} // namespace vnotch::services::loopback

namespace lb = vnotch::services::loopback;

extern "C" {

// Starts loopback capture of the requested endpoint (empty = default). out_format is the endpoint mix format.
VN_EXPORT int32_t __stdcall vn_loopback_start(
    const char16_t* device_id, int32_t device_id_length, lb::DataCallback on_data, lb::StoppedCallback on_stopped, int64_t context,
    int64_t* out_handle, lb::VnLoopbackFormat* out_format, int32_t* out_hresult) noexcept {
    if (!on_data || !out_handle || !out_format || !out_hresult || device_id_length < 0 || device_id_length > 1024 ||
        (device_id_length > 0 && !device_id))
        return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    *out_hresult = 0;
    try {
        lb::EnsureMta();
        auto capture = std::make_unique<lb::Capture>();
        lb::ComPtr<IMMDeviceEnumerator> enumerator;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(enumerator.Put()));
        if (SUCCEEDED(hr))
            hr = lb::ResolveDevice(enumerator.ptr, std::wstring(reinterpret_cast<const wchar_t*>(device_id), static_cast<size_t>(device_id_length)), capture->device);
        if (SUCCEEDED(hr))
            hr = capture->device.ptr->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(capture->client.Put()));
        WAVEFORMATEX* mix = nullptr;
        if (SUCCEEDED(hr)) hr = capture->client.ptr->GetMixFormat(&mix);
        if (SUCCEEDED(hr)) {
            capture->format.sample_rate = static_cast<int32_t>(mix->nSamplesPerSec);
            capture->format.channels = mix->nChannels;
            capture->format.bits_per_sample = mix->wBitsPerSample;
            capture->block_align = mix->nBlockAlign;
            capture->format.is_float = (mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
                (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                 reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mix)->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)) ? 1 : 0;
            hr = capture->client.ptr->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, lb::kBufferDuration, 0, mix, nullptr);
        }
        if (mix) CoTaskMemFree(mix);
        if (SUCCEEDED(hr)) hr = capture->client.ptr->GetService(__uuidof(IAudioCaptureClient), reinterpret_cast<void**>(capture->capture.Put()));
        if (FAILED(hr) || capture->block_align <= 0) {
            *out_hresult = static_cast<int32_t>(FAILED(hr) ? hr : E_FAIL);
            return VN_ERR_FAIL;
        }

        capture->device_id = lb::DeviceId(capture->device.ptr);
        capture->on_data = on_data;
        capture->on_stopped = on_stopped;
        capture->context = context;
        *out_format = capture->format;

        hr = capture->client.ptr->Start();
        if (FAILED(hr)) {
            *out_hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }
        capture->thread = CreateThread(nullptr, 0, lb::Capture::Main, capture.get(), 0, nullptr);
        if (!capture->thread) {
            *out_hresult = static_cast<int32_t>(HRESULT_FROM_WIN32(GetLastError()));
            return VN_ERR_FAIL;
        }

        lb::Guard guard(&lb::g_lock);
        for (size_t i = 0; i < lb::g_captures.size(); ++i) {
            if (!lb::g_captures[i]) {
                lb::g_captures[i] = std::move(capture);
                *out_handle = static_cast<int64_t>(i) + 1;
                return VN_OK;
            }
        }
        lb::g_captures.push_back(std::move(capture));
        *out_handle = static_cast<int64_t>(lb::g_captures.size());
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Stops and releases the capture. Must not be called from inside the data or stopped callback.
VN_EXPORT int32_t __stdcall vn_loopback_stop(int64_t handle) noexcept {
    std::unique_ptr<lb::Capture> capture;
    {
        lb::Guard guard(&lb::g_lock);
        if (handle <= 0 || static_cast<size_t>(handle) > lb::g_captures.size()) return VN_ERR_INVALID_ARG;
        capture = std::move(lb::g_captures[static_cast<size_t>(handle) - 1]);
    }
    capture.reset();
    return VN_OK;
}

// Endpoint id and whether that endpoint is still active. Two-step: BUFFER_TOO_SMALL returns the length.
VN_EXPORT int32_t __stdcall vn_loopback_get_device(
    int64_t handle, char16_t* buffer, int32_t capacity, int32_t* out_length, uint8_t* out_active) noexcept {
    lb::Capture* capture = lb::Find(handle);
    if (!capture || !out_length || !out_active || capacity < 0 || (capacity > 0 && !buffer)) return VN_ERR_INVALID_ARG;
    DWORD state = 0;
    *out_active = (capture->device.ptr && SUCCEEDED(capture->device.ptr->GetState(&state)) && state == DEVICE_STATE_ACTIVE) ? 1 : 0;
    *out_length = static_cast<int32_t>(capture->device_id.size());
    if (*out_length > capacity) return VN_ERR_BUFFER_TOO_SMALL;
    std::copy(capture->device_id.begin(), capture->device_id.end(), reinterpret_cast<wchar_t*>(buffer));
    return VN_OK;
}

// Id the capture would use for the requested endpoint (the selected one when active, else the default).
VN_EXPORT int32_t __stdcall vn_loopback_resolve_device_id(
    const char16_t* requested, int32_t requested_length, char16_t* buffer, int32_t capacity, int32_t* out_length) noexcept {
    if (!out_length || requested_length < 0 || requested_length > 1024 || (requested_length > 0 && !requested) || capacity < 0 ||
        (capacity > 0 && !buffer))
        return VN_ERR_INVALID_ARG;
    try {
        lb::EnsureMta();
        lb::ComPtr<IMMDeviceEnumerator> enumerator;
        lb::ComPtr<IMMDevice> device;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(enumerator.Put()));
        if (SUCCEEDED(hr))
            hr = lb::ResolveDevice(enumerator.ptr, std::wstring(reinterpret_cast<const wchar_t*>(requested), static_cast<size_t>(requested_length)), device);
        if (FAILED(hr)) return VN_ERR_FAIL;
        const std::wstring id = lb::DeviceId(device.ptr);
        *out_length = static_cast<int32_t>(id.size());
        if (*out_length > capacity) return VN_ERR_BUFFER_TOO_SMALL;
        std::copy(id.begin(), id.end(), reinterpret_cast<wchar_t*>(buffer));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
