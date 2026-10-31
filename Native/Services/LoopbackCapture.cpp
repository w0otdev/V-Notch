#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include "../vnotch_handle_registry.h"
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
    DWORD thread_id = 0;
    HANDLE stop_event = nullptr;
    DataCallback on_data = nullptr;
    StoppedCallback on_stopped = nullptr;
    int64_t context = 0;
    SRWLOCK stop_lock = SRWLOCK_INIT;
    std::atomic<bool> stopping{false};
    std::atomic<bool> self_delete{false};

    Capture() noexcept { stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr); }
    Capture(const Capture&) = delete;
    Capture& operator=(const Capture&) = delete;
    ~Capture() {
        Stop();
        if (stop_event) CloseHandle(stop_event);
    }

    void Stop() noexcept {
        stopping.store(true, std::memory_order_release);
        if (stop_event) SetEvent(stop_event);
        if (GetCurrentThreadId() == thread_id) return;
        Guard guard(&stop_lock);
        if (thread) {
            WaitForSingleObject(thread, INFINITE);
            CloseHandle(thread);
            thread = nullptr;
        }
        if (client.ptr) client.ptr->Stop();
    }

    // Grows the zero filled buffer used for AUDCLNT_BUFFERFLAGS_SILENT packets. False when the allocation fails.
    static bool EnsureSilence(std::vector<uint8_t>& silence, size_t bytes) noexcept {
        if (silence.size() >= bytes) return true;
        try {
            silence.assign(bytes, 0);
            return true;
        } catch (...) {
            return false;
        }
    }

    void Run() noexcept {
        EnsureMta();
        // Sized for a full endpoint buffer up front so the capture loop normally never allocates.
        std::vector<uint8_t> silence;
        HRESULT failure = S_OK;
        UINT32 buffer_frames = 0;
        if (SUCCEEDED(client.ptr->GetBufferSize(&buffer_frames)) &&
            !EnsureSilence(silence, static_cast<size_t>(buffer_frames) * static_cast<size_t>(block_align))) {
            failure = E_OUTOFMEMORY;
        }
        while (SUCCEEDED(failure) && WaitForSingleObject(stop_event, kPollMs) == WAIT_TIMEOUT) {
            UINT32 packet = 0;
            HRESULT hr = capture.ptr->GetNextPacketSize(&packet);
            while (SUCCEEDED(hr) && packet > 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = capture.ptr->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (FAILED(hr)) break;
                const int32_t bytes = static_cast<int32_t>(frames) * block_align;
                if (!stopping.load(std::memory_order_acquire) && on_data) {
                    if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
                        if (!EnsureSilence(silence, static_cast<size_t>(bytes))) {
                            // Give the packet back and stop with an error instead of terminating the process.
                            capture.ptr->ReleaseBuffer(frames);
                            hr = E_OUTOFMEMORY;
                            break;
                        }
                        on_data(context, silence.data(), bytes);
                    } else {
                        on_data(context, data, bytes);
                    }
                }
                capture.ptr->ReleaseBuffer(frames);
                if (stopping.load(std::memory_order_acquire)) break;
                hr = capture.ptr->GetNextPacketSize(&packet);
            }
            if (FAILED(hr) || stopping.load(std::memory_order_acquire)) {
                failure = hr;
                break;
            }
        }
        if (FAILED(failure) && !stopping.load(std::memory_order_acquire) && on_stopped) on_stopped(context, static_cast<int32_t>(failure));
    }

    static DWORD WINAPI Main(LPVOID param) noexcept {
        auto* self = static_cast<Capture*>(param);
        self->Run();
        if (self->self_delete.load(std::memory_order_acquire)) {
            if (self->client.ptr) self->client.ptr->Stop();
            if (self->thread) {
                CloseHandle(self->thread);
                self->thread = nullptr;
            }
            delete self;
        }
        return 0;
    }
};

// Exports that read a capture hold a lease, so a concurrent vn_loopback_stop cannot free it underneath them.
vnotch::HandleRegistry<Capture> g_captures;

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
        if (!capture->stop_event) return VN_ERR_FAIL;
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
        capture->thread = CreateThread(nullptr, 0, lb::Capture::Main, capture.get(), 0, &capture->thread_id);
        if (!capture->thread) {
            *out_hresult = static_cast<int32_t>(HRESULT_FROM_WIN32(GetLastError()));
            return VN_ERR_FAIL;
        }

        *out_handle = lb::g_captures.Add(std::move(capture));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Stops and releases the capture. Safe from any thread, including callbacks.
VN_EXPORT int32_t __stdcall vn_loopback_stop(int64_t handle) noexcept {
    if (handle <= 0) return VN_ERR_INVALID_ARG;
    {
        // Stop and join the worker while holding a lease so no callback is still in flight when this returns,
        // even if another export holds a concurrent read lease on the capture.
        auto lease = lb::g_captures.Acquire(handle);
        if (!lease) return VN_OK;  // already stopped
        if (GetCurrentThreadId() != lease->thread_id) {
            lease->Stop();
        } else {
            lease->self_delete.store(true, std::memory_order_release);
            lease->stopping.store(true, std::memory_order_release);
            if (lease->stop_event) SetEvent(lease->stop_event);
        }
    }
    std::unique_ptr<lb::Capture> capture;
    if (!lb::g_captures.Remove(handle, &capture) || !capture) return VN_OK;
    if (capture->self_delete.load(std::memory_order_acquire) || GetCurrentThreadId() == capture->thread_id) {
        capture->self_delete.store(true, std::memory_order_release);
        capture.release();
        return VN_OK;
    }
    capture.reset();
    return VN_OK;
}

// Endpoint id and whether that endpoint is still active. Two-step: BUFFER_TOO_SMALL returns the length.
VN_EXPORT int32_t __stdcall vn_loopback_get_device(
    int64_t handle, char16_t* buffer, int32_t capacity, int32_t* out_length, uint8_t* out_active) noexcept {
    auto lease = lb::g_captures.Acquire(handle);
    lb::Capture* capture = lease.get();
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
