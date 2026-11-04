#include "../vnotch_common.h"
#include "../vnotch_handle_registry.h"
#include "../vnotch_com.h"
#include <mmdeviceapi.h>
#include <endpointvolume.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propsys.h>
#include <propvarutil.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "propsys.lib")

// Audio endpoint lists (render and capture) and the default capture volume.

namespace vnotch::services::audiodevices {

using vnotch::com::ComPtr;
using vnotch::com::EnsureMta;
using vnotch::com::Guard;

enum StringKind : int32_t { kId = 0, kFriendlyName = 1 };

struct DeviceEntry {
    std::wstring id;
    std::wstring friendly_name;
    bool is_default = false;
};

struct DeviceList {
    std::vector<DeviceEntry> devices;
};

vnotch::HandleRegistry<DeviceList> g_lists;

constexpr ULONGLONG kCaptureCacheLifetimeMs = 3000;
SRWLOCK g_capture_lock = SRWLOCK_INIT;
ComPtr<IAudioEndpointVolume> g_capture_volume;
ULONGLONG g_capture_tick = 0;

std::wstring ReadFriendlyName(IMMDevice* device) {
    ComPtr<IPropertyStore> store;
    if (FAILED(device->OpenPropertyStore(STGM_READ, store.Put()))) return {};
    PROPVARIANT value;
    PropVariantInit(&value);
    struct Guard { PROPVARIANT* p; ~Guard() noexcept { PropVariantClear(p); } } guard{&value};
    std::wstring name;
    if (SUCCEEDED(store.ptr->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
        name = value.pwszVal;

    return name;
}

bool ReadId(IMMDevice* device, std::wstring& value) {
    value.clear();
    if (!device) return false;
    LPWSTR id = nullptr;
    struct Guard { LPWSTR& p; ~Guard() noexcept { if (p) CoTaskMemFree(p); } } guard{id};
    if (FAILED(device->GetId(&id)) || !id) return false;
    value.assign(id);
    return true;
}

std::wstring ReadId(IMMDevice* device) {
    LPWSTR id = nullptr;
    std::wstring result;
    struct Guard { LPWSTR& p; ~Guard() noexcept { if (p) CoTaskMemFree(p); } } guard{id}; if (device && SUCCEEDED(device->GetId(&id)) && id) {
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

// Lists the active endpoints of one data flow (0 render, 1 capture). out_hresult carries the failing HRESULT.
VN_EXPORT int32_t __stdcall vn_audio_devices_open(int32_t flow, int64_t* out_list, int32_t* out_count, int32_t* out_hresult) noexcept {
    if (!out_list || !out_count || !out_hresult || (flow != 0 && flow != 1)) return VN_ERR_INVALID_ARG;
    *out_list = 0;
    *out_count = 0;
    *out_hresult = 0;
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

        UINT count = 0;
        if (SUCCEEDED(hr)) hr = enumerator.ptr->EnumAudioEndpoints(data_flow, DEVICE_STATE_ACTIVE, collection.Put());
        if (SUCCEEDED(hr)) hr = collection.ptr->GetCount(&count);
        if (FAILED(hr)) {
            *out_hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }

        auto list = std::make_unique<ad::DeviceList>();
        list->devices.reserve(count);
        for (UINT i = 0; i < count; ++i) {
            ad::ComPtr<IMMDevice> device;
            if (FAILED(collection.ptr->Item(i, device.Put())) || !device.ptr) continue;
            ad::DeviceEntry entry;
            entry.id = ad::ReadId(device.ptr);
            entry.friendly_name = ad::ReadFriendlyName(device.ptr);
            entry.is_default = !default_id.empty() &&
                               CompareStringOrdinal(entry.id.c_str(), -1, default_id.c_str(), -1, TRUE) == CSTR_EQUAL;
            list->devices.push_back(std::move(entry));
        }
        *out_count = static_cast<int32_t>(list->devices.size());

        *out_list = ad::g_lists.Add(std::move(list));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_audio_devices_close(int64_t list) noexcept {
    if (!ad::g_lists.Remove(list)) return VN_ERR_INVALID_ARG;
    return VN_OK;
}

// kind: 0 endpoint id, 1 friendly name. Two-step: BUFFER_TOO_SMALL returns the required length.
VN_EXPORT int32_t __stdcall vn_audio_device_get_string(
    int64_t list, int32_t index, int32_t kind, char16_t* buffer, int32_t capacity, int32_t* out_length, uint8_t* out_is_default) noexcept {
    if (!out_length || !out_is_default || capacity < 0 || (capacity > 0 && !buffer) || (kind != 0 && kind != 1)) return VN_ERR_INVALID_ARG;
    auto lease = ad::g_lists.Acquire(list);
    if (!lease) return VN_ERR_INVALID_ARG;
    const auto& devices = lease->devices;
    if (index < 0 || static_cast<size_t>(index) >= devices.size()) return VN_ERR_INVALID_ARG;
    const ad::DeviceEntry& entry = devices[static_cast<size_t>(index)];
    *out_is_default = entry.is_default ? 1 : 0;
    const std::wstring& text = kind == ad::kId ? entry.id : entry.friendly_name;
    *out_length = static_cast<int32_t>(text.size());
    if (static_cast<int32_t>(text.size()) > capacity) return VN_ERR_BUFFER_TOO_SMALL;
    std::copy(text.begin(), text.end(), reinterpret_cast<wchar_t*>(buffer));
    return VN_OK;
}

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

} // extern "C"

VN_EXPORT int32_t __stdcall vn_audio_get_devices_batch(int64_t list, VnAudioDeviceState* output, int32_t capacity,
    char16_t* text, int32_t text_capacity, int32_t* count, int32_t* text_length) noexcept {
    if (!count || !text_length || capacity < 0 || text_capacity < 0 || (capacity && !output) || (text_capacity && !text)) return VN_ERR_INVALID_ARG;
    *count = *text_length = 0;
    try {
        auto lease = ad::g_lists.Acquire(list);
        if (!lease) return VN_ERR_INVALID_ARG;
        size_t required = 0;
        for (const auto& device : lease->devices) required += device.id.size() + device.friendly_name.size();
        if (required > INT32_MAX || lease->devices.size() > INT32_MAX) return VN_ERR_FAIL;
        *count = static_cast<int32_t>(lease->devices.size()); *text_length = static_cast<int32_t>(required);
        if (*count > capacity || *text_length > text_capacity) return VN_ERR_BUFFER_TOO_SMALL;
        int32_t offset = 0;
        for (size_t i = 0; i < lease->devices.size(); ++i) {
            const auto& device = lease->devices[i];
            auto& out = output[i]; out = {};
            out.id_offset = offset; out.id_length = static_cast<int32_t>(device.id.size());
            if (out.id_length) std::memcpy(text + offset, device.id.data(), device.id.size() * sizeof(char16_t));
            offset += out.id_length;
            out.name_offset = offset; out.name_length = static_cast<int32_t>(device.friendly_name.size());
            if (out.name_length) std::memcpy(text + offset, device.friendly_name.data(), device.friendly_name.size() * sizeof(char16_t));
            offset += out.name_length;
            out.is_default = device.is_default ? 1 : 0;
        }
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_audio_enumerate_devices_batch(int32_t flow, VnAudioDeviceState* output, int32_t capacity,
    char16_t* text, int32_t text_capacity, int32_t* count, int32_t* text_length, int32_t* hresult) noexcept {
    if (!count || !text_length || !hresult || capacity < 0 || text_capacity < 0 || (capacity && !output) || (text_capacity && !text)) return VN_ERR_INVALID_ARG;
    *count = *text_length = *hresult = 0;
    int64_t list = 0;
    const auto status = vn_audio_devices_open(flow, &list, count, hresult);
    if (status != VN_OK) return status;
    struct Close { int64_t handle; ~Close() { vn_audio_devices_close(handle); } } close{list};
    return vn_audio_get_devices_batch(list, output, capacity, text, text_capacity, count, text_length);
}
