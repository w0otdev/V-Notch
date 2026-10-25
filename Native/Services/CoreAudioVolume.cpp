#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include "../vnotch_handle_registry.h"
#include <objbase.h>
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <atomic>
#include <cstdint>
#include <memory>
#include <vector>

#pragma comment(lib, "ole32.lib")

// Master volume and mute of the default render endpoint through Core Audio, plus the default-device
// change notification. The managed VolumeService keeps its locking and retry policy.

namespace vnotch::services::coreaudio {

using DeviceChangedCallback = void(__stdcall*)(int64_t context);

using vnotch::com::ComPtr;
using vnotch::com::EnsureMta;
using vnotch::com::Guard;

// Notification sink owned by its Source; the reference count never frees it.
class NotificationClient final : public IMMNotificationClient {
public:
    DeviceChangedCallback callback = nullptr;
    int64_t context = 0;

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (!object) return E_POINTER;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IMMNotificationClient)) {
            *object = static_cast<IMMNotificationClient*>(this);
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override {
        // Only the render/multimedia default drives the volume control.
        if (flow == eRender && role == eMultimedia && callback) callback(context);
        return S_OK;
    }
};

struct Source {
    ComPtr<IMMDeviceEnumerator> enumerator;
    NotificationClient client;
    bool registered = false;

    ~Source() {
        if (registered && enumerator.ptr) enumerator.ptr->UnregisterEndpointNotificationCallback(&client);
    }
};

struct Endpoint {
    ComPtr<IMMDevice> device;
    ComPtr<IAudioEndpointVolume> volume;
};

// Exports hold a lease while they use an object, so closing a handle on another thread cannot free it mid-call.
vnotch::HandleRegistry<Source> g_sources;
vnotch::HandleRegistry<Endpoint> g_endpoints;


} // namespace vnotch::services::coreaudio

namespace ca = vnotch::services::coreaudio;

extern "C" {

// Creates the enumerator and registers for default-device changes. callback runs on a Core Audio thread
// and must only record that a change happened.
VN_EXPORT int32_t __stdcall vn_volume_source_create(ca::DeviceChangedCallback callback, int64_t context, int64_t* out_handle, int32_t* out_hresult) noexcept {
    if (!callback || !out_handle || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    *out_hresult = 0;
    try {
        ca::EnsureMta();
        auto source = std::make_unique<ca::Source>();
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(source->enumerator.Put()));
        if (SUCCEEDED(hr)) {
            source->client.callback = callback;
            source->client.context = context;
            hr = source->enumerator.ptr->RegisterEndpointNotificationCallback(&source->client);
            source->registered = SUCCEEDED(hr);
        }
        *out_hresult = static_cast<int32_t>(hr);
        if (FAILED(hr)) return VN_ERR_FAIL;
        *out_handle = ca::g_sources.Add(std::move(source));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_volume_source_destroy(int64_t handle) noexcept {
    if (handle <= 0) return VN_ERR_INVALID_ARG;
    ca::g_sources.Remove(handle);
    return VN_OK;
}

// Opens the default render/multimedia endpoint volume. out_hresult carries the failing HRESULT.
VN_EXPORT int32_t __stdcall vn_volume_endpoint_open(int64_t source_handle, int64_t* out_handle, int32_t* out_hresult) noexcept {
    if (!out_handle || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    *out_hresult = 0;
    auto source_lease = ca::g_sources.Acquire(source_handle);
    ca::Source* source = source_lease.get();
    if (!source || !source->enumerator.ptr) return VN_ERR_INVALID_ARG;
    try {
        auto endpoint = std::make_unique<ca::Endpoint>();
        HRESULT hr = source->enumerator.ptr->GetDefaultAudioEndpoint(eRender, eMultimedia, endpoint->device.Put());
        if (SUCCEEDED(hr))
            hr = endpoint->device.ptr->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
                                                reinterpret_cast<void**>(endpoint->volume.Put()));
        *out_hresult = static_cast<int32_t>(hr);
        if (FAILED(hr)) return VN_ERR_FAIL;
        *out_handle = ca::g_endpoints.Add(std::move(endpoint));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_volume_endpoint_close(int64_t handle) noexcept {
    if (handle <= 0) return VN_ERR_INVALID_ARG;
    ca::g_endpoints.Remove(handle);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_volume_endpoint_get(int64_t handle, float* out_scalar, uint8_t* out_muted, int32_t* out_hresult) noexcept {
    auto lease = ca::g_endpoints.Acquire(handle);
    ca::Endpoint* endpoint = lease.get();
    if (!endpoint || !endpoint->volume.ptr || !out_scalar || !out_muted || !out_hresult) return VN_ERR_INVALID_ARG;
    BOOL muted = FALSE;
    HRESULT hr = endpoint->volume.ptr->GetMasterVolumeLevelScalar(out_scalar);
    if (SUCCEEDED(hr)) hr = endpoint->volume.ptr->GetMute(&muted);
    *out_muted = muted ? 1 : 0;
    *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_volume_endpoint_set_volume(int64_t handle, float scalar, int32_t* out_hresult) noexcept {
    auto lease = ca::g_endpoints.Acquire(handle);
    ca::Endpoint* endpoint = lease.get();
    if (!endpoint || !endpoint->volume.ptr || !out_hresult) return VN_ERR_INVALID_ARG;
    const HRESULT hr = endpoint->volume.ptr->SetMasterVolumeLevelScalar(scalar, nullptr);
    *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_volume_endpoint_set_mute(int64_t handle, uint8_t muted, int32_t* out_hresult) noexcept {
    auto lease = ca::g_endpoints.Acquire(handle);
    ca::Endpoint* endpoint = lease.get();
    if (!endpoint || !endpoint->volume.ptr || !out_hresult) return VN_ERR_INVALID_ARG;
    const HRESULT hr = endpoint->volume.ptr->SetMute(muted != 0, nullptr);
    *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

} // extern "C"
