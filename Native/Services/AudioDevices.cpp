#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propsys.h>
#include <propvarutil.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "propsys.lib")

// Audio endpoint enumeration (render and capture) and the default capture volume.

namespace vnotch::services::audiodevices {

using vnotch::com::ComPtr;
using vnotch::com::EnsureMta;
using vnotch::com::Guard;

struct DeviceEntry {
    std::wstring id;
    std::wstring friendly_name;
    bool is_default = false;
};

constexpr ULONGLONG kCaptureCacheLifetimeMs = 3000;
SRWLOCK g_capture_lock = SRWLOCK_INIT;
ComPtr<IAudioEndpointVolume> g_capture_volume;
ULONGLONG g_capture_tick = 0;

std::wstring ReadFriendlyName(IMMDevice* device) {
    ComPtr<IPropertyStore> store;
    if (FAILED(device->OpenPropertyStore(STGM_READ, store.Put()))) return {};
    PROPVARIANT value;
    PropVariantInit(&value);
    struct PropGuard { PROPVARIANT* p; ~PropGuard() noexcept { PropVariantClear(p); } } guard{&value};
    std::wstring name;
    if (SUCCEEDED(store.ptr->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
        name = value.pwszVal;
    return name;
}

std::wstring ReadId(IMMDevice* device) {
    LPWSTR id = nullptr;
    std::wstring result;
    struct CoMemGuard { LPWSTR& p; ~CoMemGuard() noexcept { if (p) CoTaskMemFree(p); } } guard{id};
    if (device && SUCCEEDED(device->GetId(&id)) && id) {
        result = id;
    }
    return result;
}

bool EnsureCaptureVolumeLocked() noexcept {
    const ULONGLONG now = GetTickCount64();
    if (g_capture_volume.ptr && now - g_capture_tick < kCaptureCacheLifetimeMs) return true;
    g_capture_volume.Reset();
    EnsureMta();
    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IMMDevice> device;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(enumerator.Put()))) ||
        FAILED(enumerator.ptr->GetDefaultAudioEndpoint(eCapture, eMultimedia, device.Put())) ||
        FAILED(device.ptr->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
                                    reinterpret_cast<void**>(g_capture_volume.Put()))))
    {
        g_capture_volume.Reset();
        return false;
    }
    g_capture_tick = now;
    return true;
}

} // namespace vnotch::services::audiodevices

namespace ad = vnotch::services::audiodevices;

extern "C" {

// Master volume of the default capture endpoint. The endpoint stays cached for a few seconds.
VN_EXPORT int32_t __stdcall vn_audio_capture_volume_get(float* out_scalar) noexcept {
    if (!out_scalar) return VN_ERR_INVALID_ARG;
    ad::Guard guard(&ad::g_capture_lock);
    if (!ad::EnsureCaptureVolumeLocked()) return VN_ERR_FAIL;
    if (FAILED(ad::g_capture_volume.ptr->GetMasterVolumeLevelScalar(out_scalar))) {
        ad::g_capture_volume.Reset();
        return VN_ERR_FAIL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_capture_volume_set(float scalar) noexcept {
    if (!std::isfinite(scalar)) return VN_ERR_INVALID_ARG;
    ad::Guard guard(&ad::g_capture_lock);
    if (!ad::EnsureCaptureVolumeLocked()) return VN_ERR_FAIL;
    if (FAILED(ad::g_capture_volume.ptr->SetMasterVolumeLevelScalar(std::clamp(scalar, 0.0f, 1.0f), nullptr))) {
        ad::g_capture_volume.Reset();
        return VN_ERR_FAIL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_capture_invalidate() noexcept {
    ad::Guard guard(&ad::g_capture_lock);
    ad::g_capture_volume.Reset();
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_enumerate_devices_batch(int32_t flow, VnAudioDeviceState* output, int32_t capacity,
    char16_t* text, int32_t text_capacity, int32_t* count, int32_t* text_length, int32_t* hresult) noexcept {
    if (!count || !text_length || !hresult || capacity < 0 || text_capacity < 0 || (capacity && !output) || (text_capacity && !text))
        return VN_ERR_INVALID_ARG;
    *count = *text_length = *hresult = 0;
    if (flow != 0 && flow != 1) return VN_ERR_INVALID_ARG;

    try {
        ad::EnsureMta();
        const EDataFlow data_flow = flow == 0 ? eRender : eCapture;
        ad::ComPtr<IMMDeviceEnumerator> enumerator;
        ad::ComPtr<IMMDeviceCollection> collection;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(enumerator.Put()));

        // A missing default endpoint is not an error: the list is still returned, with nothing marked default.
        std::wstring default_id;
        if (SUCCEEDED(hr)) {
            ad::ComPtr<IMMDevice> default_device;
            if (SUCCEEDED(enumerator.ptr->GetDefaultAudioEndpoint(data_flow, eMultimedia, default_device.Put())) && default_device.ptr)
                default_id = ad::ReadId(default_device.ptr);
        }

        UINT raw_count = 0;
        if (SUCCEEDED(hr)) hr = enumerator.ptr->EnumAudioEndpoints(data_flow, DEVICE_STATE_ACTIVE, collection.Put());
        if (SUCCEEDED(hr)) hr = collection.ptr->GetCount(&raw_count);
        if (FAILED(hr)) {
            *hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }

        std::vector<ad::DeviceEntry> devices;
        devices.reserve(raw_count);
        size_t required = 0;
        for (UINT i = 0; i < raw_count; ++i) {
            ad::ComPtr<IMMDevice> device;
            if (FAILED(collection.ptr->Item(i, device.Put())) || !device.ptr) continue;
            ad::DeviceEntry entry;
            entry.id = ad::ReadId(device.ptr);
            entry.friendly_name = ad::ReadFriendlyName(device.ptr);
            entry.is_default = !default_id.empty() &&
                               CompareStringOrdinal(entry.id.c_str(), -1, default_id.c_str(), -1, TRUE) == CSTR_EQUAL;
            required += entry.id.size() + entry.friendly_name.size();
            devices.push_back(std::move(entry));
        }

        if (required > INT32_MAX || devices.size() > INT32_MAX) return VN_ERR_FAIL;
        *count = static_cast<int32_t>(devices.size());
        *text_length = static_cast<int32_t>(required);
        if (*count > capacity || *text_length > text_capacity) return VN_ERR_BUFFER_TOO_SMALL;

        int32_t offset = 0;
        for (size_t i = 0; i < devices.size(); ++i) {
            const auto& device = devices[i];
            auto& out = output[i];
            out = {};
            out.id_offset = offset;
            out.id_length = static_cast<int32_t>(device.id.size());
            if (out.id_length) std::memcpy(text + offset, device.id.data(), device.id.size() * sizeof(char16_t));
            offset += out.id_length;
            out.name_offset = offset;
            out.name_length = static_cast<int32_t>(device.friendly_name.size());
            if (out.name_length) std::memcpy(text + offset, device.friendly_name.data(), device.friendly_name.size() * sizeof(char16_t));
            offset += out.name_length;
            out.is_default = device.is_default ? 1 : 0;
        }
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
