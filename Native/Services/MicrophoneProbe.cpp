#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include "../vnotch_handle_registry.h"
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <algorithm>
#include <cstdint>
#include <memory>
#include <vector>

#pragma comment(lib, "ole32.lib")

// Microphone activity probe: tracks active, unmuted capture sessions and samples their peak meters.
// The managed privacy indicator decides which processes count (it owns the process matching) and when
// to rebuild; this file owns every Core Audio object.

namespace vnotch::services::micprobe {

using vnotch::com::ComPtr;
using vnotch::com::EnsureMta;
using vnotch::com::Guard;

using InvalidatedCallback = void(__stdcall*)(int64_t context);

struct MeterEntry {
    ComPtr<IAudioSessionControl2> control;
    ComPtr<ISimpleAudioVolume> session_volume;
    ComPtr<IAudioMeterInformation> session_meter;
    ComPtr<IAudioEndpointVolume> endpoint_volume;
    ComPtr<IAudioMeterInformation> endpoint_meter;
    DWORD process_id = 0;
};

// Any endpoint topology change invalidates the cached meters. The reference count never frees the sink.
class NotificationClient final : public IMMNotificationClient {
public:
    InvalidatedCallback callback = nullptr;
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
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override { Notify(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override { Notify(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override { Notify(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow, ERole, LPCWSTR) override { Notify(); return S_OK; }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }

private:
    void Notify() noexcept {
        if (callback) callback(context);
    }
};

struct ProbeLockGuard {
    SRWLOCK* lock;
    explicit ProbeLockGuard(SRWLOCK* l) noexcept : lock(l) { AcquireSRWLockExclusive(lock); }
    ~ProbeLockGuard() { ReleaseSRWLockExclusive(lock); }
};

struct Probe {
    SRWLOCK lock = SRWLOCK_INIT;
    ComPtr<IMMDeviceEnumerator> enumerator;
    NotificationClient client;
    bool registered = false;
    std::vector<std::unique_ptr<MeterEntry>> entries;

    ~Probe() {
        if (registered && enumerator.ptr) enumerator.ptr->UnregisterEndpointNotificationCallback(&client);
    }
};

vnotch::HandleRegistry<Probe> g_probes;

bool EndpointMuted(IAudioEndpointVolume* volume) noexcept {
    BOOL muted = FALSE;
    // Some virtual endpoints expose no mute switch; treat that as not muted.
    return volume && SUCCEEDED(volume->GetMute(&muted)) && muted;
}

void CollectDevice(IMMDevice* device, std::vector<std::unique_ptr<MeterEntry>>& out) {
    ComPtr<IAudioEndpointVolume> endpoint_volume;
    if (SUCCEEDED(device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(endpoint_volume.Put()))) &&
        EndpointMuted(endpoint_volume.ptr))
        return;

    ComPtr<IAudioSessionManager2> manager;
    ComPtr<IAudioSessionEnumerator> sessions;
    if (FAILED(device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(manager.Put()))) ||
        FAILED(manager.ptr->GetSessionEnumerator(sessions.Put())))
        return;
    int count = 0;
    if (FAILED(sessions.ptr->GetCount(&count))) return;

    ComPtr<IAudioMeterInformation> endpoint_meter;
    device->Activate(__uuidof(IAudioMeterInformation), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(endpoint_meter.Put()));

    for (int i = 0; i < count; ++i) {
        ComPtr<IAudioSessionControl> basic;
        if (FAILED(sessions.ptr->GetSession(i, basic.Put())) || !basic.ptr) continue;
        AudioSessionState state = AudioSessionStateInactive;
        if (FAILED(basic.ptr->GetState(&state)) || state != AudioSessionStateActive) continue;

        auto entry = std::make_unique<MeterEntry>();
        if (FAILED(basic.ptr->QueryInterface(IID_PPV_ARGS(entry->control.Put())))) continue;
        entry->control.ptr->GetProcessId(&entry->process_id);
        basic.ptr->QueryInterface(IID_PPV_ARGS(entry->session_volume.Put()));
        BOOL muted = FALSE;
        if (entry->session_volume.ptr && SUCCEEDED(entry->session_volume.ptr->GetMute(&muted)) && muted) continue;
        basic.ptr->QueryInterface(IID_PPV_ARGS(entry->session_meter.Put()));
        if (endpoint_volume.ptr) {
            entry->endpoint_volume.ptr = endpoint_volume.ptr;
            entry->endpoint_volume.ptr->AddRef();
        }
        if (endpoint_meter.ptr) {
            entry->endpoint_meter.ptr = endpoint_meter.ptr;
            entry->endpoint_meter.ptr->AddRef();
        }
        out.push_back(std::move(entry));
    }
}

// Same validity rules as the former managed probe: inactive or failing sessions are invalid, muted ones count
// as active with no signal, and the endpoint meter is consulted only when the session meter reads zero.
bool SampleEntry(const MeterEntry& entry, float& peak) noexcept {
    peak = 0.0f;
    AudioSessionState state = AudioSessionStateInactive;
    if (!entry.control.ptr || FAILED(entry.control.ptr->GetState(&state)) || state != AudioSessionStateActive) return false;

    BOOL muted = FALSE;
    if (entry.session_volume.ptr) {
        if (FAILED(entry.session_volume.ptr->GetMute(&muted))) return false;
        if (muted) return true;
    }
    if (EndpointMuted(entry.endpoint_volume.ptr)) return true;

    float session_peak = 0.0f;
    if (entry.session_meter.ptr && FAILED(entry.session_meter.ptr->GetPeakValue(&session_peak))) session_peak = 0.0f;
    float endpoint_peak = 0.0f;
    if (session_peak <= 0.0f && entry.endpoint_meter.ptr && FAILED(entry.endpoint_meter.ptr->GetPeakValue(&endpoint_peak)))
        endpoint_peak = 0.0f;
    peak = std::max(session_peak, endpoint_peak);
    return true;
}

} // namespace vnotch::services::micprobe

namespace mp = vnotch::services::micprobe;

extern "C" {

VN_EXPORT int32_t __stdcall vn_mic_probe_create(mp::InvalidatedCallback callback, int64_t context, int64_t* out_handle, int32_t* out_hresult) noexcept {
    if (!callback || !out_handle || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_handle = 0;
    *out_hresult = 0;
    try {
        mp::EnsureMta();
        auto probe = std::make_unique<mp::Probe>();
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(probe->enumerator.Put()));
        if (SUCCEEDED(hr)) {
            probe->client.callback = callback;
            probe->client.context = context;
            hr = probe->enumerator.ptr->RegisterEndpointNotificationCallback(&probe->client);
            probe->registered = SUCCEEDED(hr);
        }
        *out_hresult = static_cast<int32_t>(hr);
        if (FAILED(hr)) return VN_ERR_FAIL;
        *out_handle = mp::g_probes.Add(std::move(probe));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_mic_probe_destroy(int64_t handle) noexcept {
    if (handle <= 0) return VN_ERR_INVALID_ARG;
    mp::g_probes.Remove(handle);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_mic_probe_clear(int64_t handle) noexcept {
    auto lease = mp::g_probes.Acquire(handle);
    mp::Probe* probe = lease.get();
    if (!probe) return VN_ERR_INVALID_ARG;
    mp::ProbeLockGuard guard(&probe->lock);
    probe->entries.clear();
    return VN_OK;
}

// Rebuilds the list from every active capture endpoint: active, unmuted sessions on unmuted endpoints.
VN_EXPORT int32_t __stdcall vn_mic_probe_rebuild(int64_t handle, int32_t* out_count, int32_t* out_hresult) noexcept {
    auto lease = mp::g_probes.Acquire(handle);
    mp::Probe* probe = lease.get();
    if (!probe || !probe->enumerator.ptr || !out_count || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_count = 0;
    *out_hresult = 0;
    mp::ProbeLockGuard guard(&probe->lock);
    try {
        probe->entries.clear();
        mp::ComPtr<IMMDeviceCollection> devices;
        HRESULT hr = probe->enumerator.ptr->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, devices.Put());
        UINT count = 0;
        if (SUCCEEDED(hr)) hr = devices.ptr->GetCount(&count);
        if (FAILED(hr)) {
            *out_hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }
        for (UINT i = 0; i < count; ++i) {
            mp::ComPtr<IMMDevice> device;
            if (SUCCEEDED(devices.ptr->Item(i, device.Put())) && device.ptr) mp::CollectDevice(device.ptr, probe->entries);
        }
        *out_count = static_cast<int32_t>(probe->entries.size());
        return VN_OK;
    } catch (...) {
        probe->entries.clear();
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_mic_probe_get_process_id(int64_t handle, int32_t index, uint32_t* out_process_id) noexcept {
    auto lease = mp::g_probes.Acquire(handle);
    mp::Probe* probe = lease.get();
    if (!probe || !out_process_id || index < 0) return VN_ERR_INVALID_ARG;
    mp::ProbeLockGuard guard(&probe->lock);
    if (static_cast<size_t>(index) >= probe->entries.size()) return VN_ERR_INVALID_ARG;
    *out_process_id = probe->entries[static_cast<size_t>(index)]->process_id;
    return VN_OK;
}

// Drops every entry whose process id is not in the list; out_count is what remains.
VN_EXPORT int32_t __stdcall vn_mic_probe_keep(int64_t handle, const uint32_t* process_ids, int32_t count, int32_t* out_count) noexcept {
    auto lease = mp::g_probes.Acquire(handle);
    mp::Probe* probe = lease.get();
    if (!probe || !out_count || count < 0 || (count > 0 && !process_ids)) return VN_ERR_INVALID_ARG;
    mp::ProbeLockGuard guard(&probe->lock);
    const uint32_t* end = process_ids + count;
    std::erase_if(probe->entries, [&](const std::unique_ptr<mp::MeterEntry>& entry) {
        return std::find(process_ids, end, static_cast<uint32_t>(entry->process_id)) == end;
    });
    *out_count = static_cast<int32_t>(probe->entries.size());
    return VN_OK;
}

// Samples every retained session. out_any_invalid = 1 when one failed or went inactive, which means the
// topology should be rebuilt. out_peak is the highest valid peak.
VN_EXPORT int32_t __stdcall vn_mic_probe_sample(
    int64_t handle, uint8_t* out_has_active, float* out_peak, uint8_t* out_any_invalid) noexcept {
    auto lease = mp::g_probes.Acquire(handle);
    mp::Probe* probe = lease.get();
    if (!probe || !out_has_active || !out_peak || !out_any_invalid) return VN_ERR_INVALID_ARG;
    mp::ProbeLockGuard guard(&probe->lock);
    *out_has_active = 0;
    *out_any_invalid = 0;
    float highest = 0.0f;
    for (size_t i = probe->entries.size(); i-- > 0;) {
        float peak = 0.0f;
        if (!mp::SampleEntry(*probe->entries[i], peak)) {
            *out_any_invalid = 1;
            continue;
        }
        *out_has_active = 1;
        if (peak > highest) highest = peak;
    }
    *out_peak = highest;
    return VN_OK;
}

} // extern "C"
