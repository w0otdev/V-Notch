#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include <winver.h>
#pragma comment(lib, "version.lib")
#include "../vnotch_handle_registry.h"
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")

// Per-application audio sessions of the default render endpoint. A snapshot owns every session it listed;
// volume leases are independent references so a caller can keep one after the snapshot is closed.

namespace vnotch::services::audiosessions {

using vnotch::com::ComPtr;
using vnotch::com::EnsureMta;
using vnotch::com::Guard;

enum StringKind : int32_t { kDisplayName = 0, kIdentifier = 1, kInstanceIdentifier = 2 };

struct Session {
    ComPtr<IAudioSessionControl2> control;
    ComPtr<ISimpleAudioVolume> volume;
    ComPtr<IAudioMeterInformation> meter;
    std::wstring display_name;
    std::wstring identifier;
    std::wstring instance_identifier;
    DWORD process_id = 0;
    int32_t state = 0;
    bool is_system_sounds = false;
};

struct Snapshot {
    std::vector<std::unique_ptr<Session>> sessions;
};

struct VolumeLease {
    ComPtr<ISimpleAudioVolume> volume;
    ComPtr<IAudioSessionControl2> control;
};

// Exports hold a registry lease while they use an object, so closing a handle on another thread cannot free it mid-call.
vnotch::HandleRegistry<Snapshot> g_snapshots;
vnotch::HandleRegistry<VolumeLease> g_leases;


std::wstring TakeString(LPWSTR text) {
    std::wstring result = text ? std::wstring(text) : std::wstring();
    if (text) CoTaskMemFree(text);
    return result;
}

} // namespace vnotch::services::audiosessions

namespace as = vnotch::services::audiosessions;



namespace vnotch::services::audiosessions {

void AppendEndpointSessions(IMMDevice* device, Snapshot& snapshot, HRESULT& hr) {
    ComPtr<IAudioSessionManager2> manager;
    ComPtr<IAudioSessionEnumerator> sessions;
    hr = device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(manager.Put()));
    if (SUCCEEDED(hr)) hr = manager.ptr->GetSessionEnumerator(sessions.Put());
    int count = 0;
    if (SUCCEEDED(hr)) hr = sessions.ptr->GetCount(&count);
    if (FAILED(hr)) return;
    for (int i = 0; i < count; ++i) {
        ComPtr<IAudioSessionControl> basic;
        if (FAILED(sessions.ptr->GetSession(i, basic.Put())) || !basic.ptr) continue;
        auto session = std::make_unique<Session>();
        if (FAILED(basic.ptr->QueryInterface(IID_PPV_ARGS(session->control.Put())))) continue;
        basic.ptr->QueryInterface(IID_PPV_ARGS(session->volume.Put()));
        basic.ptr->QueryInterface(IID_PPV_ARGS(session->meter.Put()));
        session->control.ptr->GetProcessId(&session->process_id);
        session->is_system_sounds = session->control.ptr->IsSystemSoundsSession() == S_OK;
        AudioSessionState state = AudioSessionStateInactive;
        if (SUCCEEDED(session->control.ptr->GetState(&state))) session->state = static_cast<int32_t>(state);
        LPWSTR text = nullptr;
        if (SUCCEEDED(session->control.ptr->GetDisplayName(&text))) session->display_name = TakeString(text);
        text = nullptr;
        if (SUCCEEDED(session->control.ptr->GetSessionIdentifier(&text))) session->identifier = TakeString(text);
        text = nullptr;
        if (SUCCEEDED(session->control.ptr->GetSessionInstanceIdentifier(&text))) session->instance_identifier = TakeString(text);
        snapshot.sessions.push_back(std::move(session));
    }
}

// Media may be routed to a non-default output in Windows Volume Mixer.
// Keep the default-only API for the mixer, but let media controls search all active outputs.
std::unique_ptr<Snapshot> BuildSnapshot(HRESULT& hr, bool all_endpoints = false) {
    EnsureMta();
    ComPtr<IMMDeviceEnumerator> enumerator;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(enumerator.Put()));
    if (FAILED(hr)) return nullptr;
    auto snapshot = std::make_unique<Snapshot>();
    if (!all_endpoints) {
        ComPtr<IMMDevice> device;
        hr = enumerator.ptr->GetDefaultAudioEndpoint(eRender, eMultimedia, device.Put());
        if (SUCCEEDED(hr)) AppendEndpointSessions(device.ptr, *snapshot, hr);
        if (FAILED(hr)) return nullptr;
    } else {
        ComPtr<IMMDeviceCollection> devices;
        hr = enumerator.ptr->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, devices.Put());
        UINT count = 0;
        if (SUCCEEDED(hr)) hr = devices.ptr->GetCount(&count);
        if (FAILED(hr)) return nullptr;
        for (UINT i = 0; i < count; ++i) {
            ComPtr<IMMDevice> device;
            HRESULT endpoint_hr = devices.ptr->Item(i, device.Put());
            if (SUCCEEDED(endpoint_hr)) AppendEndpointSessions(device.ptr, *snapshot, endpoint_hr);
            // An endpoint can disappear during enumeration; other outputs are still usable.
        }
    }
    return snapshot;
}

// Session cache for volume changes: the same process is usually adjusted repeatedly while a slider moves.
constexpr ULONGLONG kSetCacheLifetimeMs = 3000;
SRWLOCK g_set_lock = SRWLOCK_INIT;
ComPtr<ISimpleAudioVolume> g_set_volume;
DWORD g_set_pid = 0;
bool g_set_system = false;
ULONGLONG g_set_tick = 0;

void InvalidateSetCacheLocked() noexcept {
    g_set_volume.Reset();
    g_set_pid = 0;
    g_set_system = false;
}

bool Matches(const Session& session, uint32_t process_id) noexcept {
    return process_id == 0 ? session.is_system_sounds : (!session.is_system_sounds && session.process_id == process_id);
}

} // namespace vnotch::services::audiosessions

extern "C" {

VN_EXPORT int32_t __stdcall vn_audio_sessions_open(int64_t* out_snapshot, int32_t* out_count, int32_t* out_hresult) noexcept {
    if (!out_snapshot || !out_count || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_snapshot = 0;
    *out_count = 0;
    *out_hresult = 0;
    try {
        HRESULT hr = S_OK;
        auto snapshot = as::BuildSnapshot(hr);
        if (!snapshot) {
            *out_hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }
        *out_count = static_cast<int32_t>(snapshot->sessions.size());
        *out_snapshot = as::g_snapshots.Add(std::move(snapshot));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_audio_sessions_open_all(int64_t* out_snapshot, int32_t* out_count, int32_t* out_hresult) noexcept {
    if (!out_snapshot || !out_count || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_snapshot = 0;
    *out_count = 0;
    *out_hresult = static_cast<int32_t>(E_FAIL);
    try {
        HRESULT hr = S_OK;
        auto snapshot = as::BuildSnapshot(hr, true);
        *out_hresult = static_cast<int32_t>(hr);
        if (!snapshot) return VN_ERR_FAIL;
        *out_count = static_cast<int32_t>(snapshot->sessions.size());
        *out_snapshot = as::g_snapshots.Add(std::move(snapshot));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

// AudioSessionState of the session: 0 inactive, 1 active, 2 expired.
VN_EXPORT int32_t __stdcall vn_audio_session_get_state(int64_t snapshot, int32_t index, int32_t* out_state) noexcept {
    auto snapshot_lease = as::g_snapshots.Acquire(snapshot);
    as::Snapshot* list = snapshot_lease.get();
    if (!list || !out_state || index < 0 || static_cast<size_t>(index) >= list->sessions.size()) return VN_ERR_INVALID_ARG;
    *out_state = list->sessions[static_cast<size_t>(index)]->state;
    return VN_OK;
}

// Sets the session volume of a process (0 = system sounds). A non-zero volume also unmutes. The matched
// session stays cached for a few seconds so slider drags do not re-enumerate every time.
VN_EXPORT int32_t __stdcall vn_audio_set_session_volume(uint32_t process_id, float scalar, int32_t* out_hresult) noexcept {
    if (!out_hresult || !std::isfinite(scalar)) return VN_ERR_INVALID_ARG;
    *out_hresult = 0;
    const float target = std::clamp(scalar, 0.0f, 1.0f);
    try {
        as::Guard guard(&as::g_set_lock);
        const ULONGLONG now = GetTickCount64();
        if (as::g_set_volume.ptr && as::g_set_pid == process_id && now - as::g_set_tick < as::kSetCacheLifetimeMs) {
            HRESULT hr = as::g_set_volume.ptr->SetMasterVolume(target, nullptr);
            BOOL muted = FALSE;
            if (SUCCEEDED(hr) && target > 0.001f && SUCCEEDED(as::g_set_volume.ptr->GetMute(&muted)) && muted)
                hr = as::g_set_volume.ptr->SetMute(FALSE, nullptr);
            if (SUCCEEDED(hr)) return VN_OK;
            as::InvalidateSetCacheLocked();
        }

        HRESULT hr = S_OK;
        auto snapshot = as::BuildSnapshot(hr);
        if (!snapshot) {
            *out_hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }
        bool updated_any = false;
        int matched_count = 0;
        ISimpleAudioVolume* last_vol = nullptr;
        for (auto& session : snapshot->sessions) {
            if (session->state == AudioSessionStateExpired || !as::Matches(*session, process_id) || !session->volume.ptr) continue;
            ++matched_count;
            HRESULT s_hr = session->volume.ptr->SetMasterVolume(target, nullptr);
            BOOL muted = FALSE;
            if (SUCCEEDED(s_hr) && target > 0.001f && SUCCEEDED(session->volume.ptr->GetMute(&muted)) && muted)
                s_hr = session->volume.ptr->SetMute(FALSE, nullptr);
            if (FAILED(s_hr)) continue;
            updated_any = true;
            if (session->state == AudioSessionStateActive || !last_vol)
                last_vol = session->volume.ptr;
        }
        if (updated_any) {
            as::InvalidateSetCacheLocked();
            if (matched_count == 1 && last_vol) {
                as::g_set_volume.Assign(last_vol);
                as::g_set_pid = process_id;
                as::g_set_tick = now;
            }
            return VN_OK;
        }
        *out_hresult = static_cast<int32_t>(E_NOTFOUND);
        return VN_ERR_FAIL;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Toggles mute for a process (0 = system sounds). out_muted is the new state.
VN_EXPORT int32_t __stdcall vn_audio_toggle_session_mute(uint32_t process_id, uint8_t* out_muted) noexcept {
    if (!out_muted) return VN_ERR_INVALID_ARG;
    *out_muted = 0;
    try {
        {
            as::Guard guard(&as::g_set_lock);
            as::InvalidateSetCacheLocked();
        }
        HRESULT hr = S_OK;
        auto snapshot = as::BuildSnapshot(hr);
        if (!snapshot) return VN_ERR_FAIL;
        BOOL target_mute = FALSE;
        bool target_determined = false;
        for (auto& session : snapshot->sessions) {
            if (session->state == AudioSessionStateExpired || !as::Matches(*session, process_id) || !session->volume.ptr) continue;
            if (!target_determined || session->state == AudioSessionStateActive) {
                BOOL muted = FALSE;
                if (SUCCEEDED(session->volume.ptr->GetMute(&muted))) {
                    target_mute = !muted;
                    target_determined = true;
                    if (session->state == AudioSessionStateActive) break;
                }
            }
        }
        if (!target_determined) return VN_ERR_FAIL;
        bool updated_any = false;
        for (auto& session : snapshot->sessions) {
            if (session->state == AudioSessionStateExpired || !as::Matches(*session, process_id) || !session->volume.ptr) continue;
            if (SUCCEEDED(session->volume.ptr->SetMute(target_mute, nullptr)))
                updated_any = true;
        }
        if (updated_any) {
            *out_muted = target_mute ? 1 : 0;
            return VN_OK;
        }
        return VN_ERR_FAIL;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_audio_invalidate_session_cache() noexcept {
    as::Guard guard(&as::g_set_lock);
    as::InvalidateSetCacheLocked();
    return VN_OK;
}
VN_EXPORT int32_t __stdcall vn_audio_sessions_close(int64_t snapshot) noexcept {
    if (snapshot <= 0) return VN_ERR_INVALID_ARG;
    as::g_snapshots.Remove(snapshot);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_session_get_info(
    int64_t snapshot, int32_t index, uint32_t* out_process_id, uint8_t* out_is_system_sounds) noexcept {
    auto snapshot_lease = as::g_snapshots.Acquire(snapshot);
    as::Snapshot* list = snapshot_lease.get();
    if (!list || !out_process_id || !out_is_system_sounds || index < 0 || static_cast<size_t>(index) >= list->sessions.size())
        return VN_ERR_INVALID_ARG;
    const as::Session& session = *list->sessions[static_cast<size_t>(index)];
    *out_process_id = session.process_id;
    *out_is_system_sounds = session.is_system_sounds ? 1 : 0;
    return VN_OK;
}

// kind: 0 display name, 1 session identifier, 2 session instance identifier. Two-step: BUFFER_TOO_SMALL
// returns the required length in out_length.
VN_EXPORT int32_t __stdcall vn_audio_session_get_string(
    int64_t snapshot, int32_t index, int32_t kind, char16_t* buffer, int32_t capacity, int32_t* out_length) noexcept {
    auto snapshot_lease = as::g_snapshots.Acquire(snapshot);
    as::Snapshot* list = snapshot_lease.get();
    if (!list || !out_length || capacity < 0 || (capacity > 0 && !buffer) || index < 0 ||
        static_cast<size_t>(index) >= list->sessions.size() || kind < 0 || kind > 2)
        return VN_ERR_INVALID_ARG;
    const as::Session& session = *list->sessions[static_cast<size_t>(index)];
    const std::wstring& text = kind == as::kDisplayName ? session.display_name
                               : kind == as::kIdentifier ? session.identifier
                                                         : session.instance_identifier;
    *out_length = static_cast<int32_t>(text.size());
    if (static_cast<int32_t>(text.size()) > capacity) return VN_ERR_BUFFER_TOO_SMALL;
    std::copy(text.begin(), text.end(), reinterpret_cast<wchar_t*>(buffer));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_session_read_peak(int64_t snapshot, int32_t index, float* out_peak) noexcept {
    auto snapshot_lease = as::g_snapshots.Acquire(snapshot);
    as::Snapshot* list = snapshot_lease.get();
    if (!list || !out_peak || index < 0 || static_cast<size_t>(index) >= list->sessions.size()) return VN_ERR_INVALID_ARG;
    const as::Session& session = *list->sessions[static_cast<size_t>(index)];
    *out_peak = 0.0f;
    if (!session.meter.ptr) return VN_ERR_FAIL;
    return SUCCEEDED(session.meter.ptr->GetPeakValue(out_peak)) ? VN_OK : VN_ERR_FAIL;
}

// Independent ISimpleAudioVolume reference for a session; release with vn_audio_volume_close.
VN_EXPORT int32_t __stdcall vn_audio_session_open_volume(int64_t snapshot, int32_t index, int64_t* out_volume) noexcept {
    auto snapshot_lease = as::g_snapshots.Acquire(snapshot);
    as::Snapshot* list = snapshot_lease.get();
    if (!list || !out_volume || index < 0 || static_cast<size_t>(index) >= list->sessions.size()) return VN_ERR_INVALID_ARG;
    *out_volume = 0;
    const as::Session& session = *list->sessions[static_cast<size_t>(index)];
    if (!session.volume.ptr) return VN_ERR_FAIL;
    try {
        auto lease = std::make_unique<as::VolumeLease>();
        lease->volume.ptr = session.volume.ptr;
        lease->volume.ptr->AddRef();
        lease->control.ptr = session.control.ptr;
        lease->control.ptr->AddRef();
        *out_volume = as::g_leases.Add(std::move(lease));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_audio_volume_get_state(int64_t volume, int32_t* out_state) noexcept {
    auto volume_lease = as::g_leases.Acquire(volume);
    as::VolumeLease* lease = volume_lease.get();
    if (!lease || !out_state) return VN_ERR_INVALID_ARG;
    AudioSessionState state = AudioSessionStateExpired;
    if (!lease->control.ptr || FAILED(lease->control.ptr->GetState(&state))) return VN_ERR_FAIL;
    *out_state = static_cast<int32_t>(state);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_volume_get(int64_t volume, float* out_scalar, uint8_t* out_muted, int32_t* out_hresult) noexcept {
    auto volume_lease = as::g_leases.Acquire(volume);
    as::VolumeLease* lease = volume_lease.get();
    if (!lease || !lease->volume.ptr || !out_scalar || !out_muted || !out_hresult) return VN_ERR_INVALID_ARG;
    BOOL muted = FALSE;
    HRESULT hr = lease->volume.ptr->GetMasterVolume(out_scalar);
    if (SUCCEEDED(hr)) hr = lease->volume.ptr->GetMute(&muted);
    *out_muted = muted ? 1 : 0;
    *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_audio_volume_set_volume(int64_t volume, float scalar, int32_t* out_hresult) noexcept {
    auto volume_lease = as::g_leases.Acquire(volume);
    as::VolumeLease* lease = volume_lease.get();
    if (!lease || !lease->volume.ptr || !out_hresult) return VN_ERR_INVALID_ARG;
    const HRESULT hr = lease->volume.ptr->SetMasterVolume(scalar, nullptr);
    *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_audio_volume_set_mute(int64_t volume, uint8_t muted, int32_t* out_hresult) noexcept {
    auto volume_lease = as::g_leases.Acquire(volume);
    as::VolumeLease* lease = volume_lease.get();
    if (!lease || !lease->volume.ptr || !out_hresult) return VN_ERR_INVALID_ARG;
    const HRESULT hr = lease->volume.ptr->SetMute(muted != 0, nullptr);
    *out_hresult = static_cast<int32_t>(hr);
    return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_audio_volume_close(int64_t volume) noexcept {
    if (volume <= 0) return VN_ERR_INVALID_ARG;
    as::g_leases.Remove(volume);
    return VN_OK;
}

} // extern "C"

namespace {
struct MixerMetadata {
    DWORD pid{};
    uint64_t started{};
    std::wstring name, path;
};
thread_local std::vector<MixerMetadata> mixer_metadata;
MixerMetadata ReadMixerMetadata(DWORD pid, const std::wstring& fallback) {
    vnotch::UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    FILETIME created{}, exited{}, kernel{}, user{};
    uint64_t started = 0;
    if (process.valid() && GetProcessTimes(process.get(), &created, &exited, &kernel, &user))
        started = (uint64_t{created.dwHighDateTime} << 32) | created.dwLowDateTime;
    for (auto& cached : mixer_metadata)
        if (started && cached.pid == pid && cached.started == started) return cached;
    MixerMetadata value{pid, started, {}, {}};
    wchar_t path[32768];
    DWORD length = ARRAYSIZE(path);
    if (process.valid() && QueryFullProcessImageNameW(process.get(), 0, path, &length)) {
        value.path.assign(path, length);
        const auto slash = value.path.find_last_of(L"\\/");
        value.name = value.path.substr(slash == std::wstring::npos ? 0 : slash + 1);
        const auto dot = value.name.find_last_of(L'.');
        if (dot != std::wstring::npos) value.name.resize(dot);
        DWORD unused = 0;
        const DWORD size = GetFileVersionInfoSizeW(value.path.c_str(), &unused);
        if (size && size <= 4 * 1024 * 1024) {
            std::vector<uint8_t> version(size);
            if (GetFileVersionInfoW(value.path.c_str(), 0, size, version.data())) {
                struct Translation { WORD language, codepage; };
                Translation* translations = nullptr;
                UINT bytes = 0;
                if (VerQueryValueW(version.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&translations), &bytes)) {
                    for (UINT i = 0; i < bytes / sizeof(Translation); ++i) {
                        wchar_t key[80];
                        swprintf_s(key, L"\\StringFileInfo\\%04x%04x\\FileDescription", translations[i].language, translations[i].codepage);
                        wchar_t* description = nullptr;
                        UINT chars = 0;
                        if (VerQueryValueW(version.data(), key, reinterpret_cast<void**>(&description), &chars) && description && chars > 1) {
                            const auto trimmed = vnotch::trim_view(std::u16string_view(reinterpret_cast<char16_t*>(description), chars - 1));
                            if (!trimmed.empty()) { value.name.assign(reinterpret_cast<const wchar_t*>(trimmed.data()), trimmed.size()); break; }
                        }
                    }
                }
            }
        }
    }
    if (value.name.empty() && !fallback.empty() && fallback.front() != L'@') value.name = fallback;
    if (started) {
        if (mixer_metadata.size() >= 128) mixer_metadata.erase(mixer_metadata.begin());
        mixer_metadata.push_back(value);
    }
    return value;
}
}

VN_EXPORT int32_t __stdcall vn_audio_resolve_session_metadata(uint32_t pid, const char16_t* raw, int32_t raw_length,
    VnAudioSessionState* output, char16_t* text, int32_t capacity, int32_t* required) noexcept {
    if (!output || !required || raw_length < 0 || (raw_length && !raw) || capacity < 0 || (capacity && !text)) return VN_ERR_INVALID_ARG;
    *output = {}; *required = 0;
    try {
        const auto metadata = ReadMixerMetadata(pid, std::wstring(reinterpret_cast<const wchar_t*>(raw ? raw : u""), raw_length));
        const auto size = metadata.name.size() + metadata.path.size();
        if (size > INT32_MAX) return VN_ERR_FAIL;
        *required = static_cast<int32_t>(size);
        if (size > static_cast<size_t>(capacity)) return VN_ERR_BUFFER_TOO_SMALL;
        output->name_length = static_cast<int32_t>(metadata.name.size());
        output->path_offset = output->name_length;
        output->path_length = static_cast<int32_t>(metadata.path.size());
        if (output->name_length) std::memcpy(text, metadata.name.data(), metadata.name.size() * sizeof(char16_t));
        if (output->path_length) std::memcpy(text + output->path_offset, metadata.path.data(), metadata.path.size() * sizeof(char16_t));
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

static int32_t ReadSessionsBatch(int64_t snapshot, VnAudioSessionState* output, int32_t capacity,
    char16_t* text, int32_t text_capacity, int32_t* count, int32_t* text_length) noexcept {
    if (!count || !text_length || capacity < 0 || text_capacity < 0 || (capacity && !output) || (text_capacity && !text)) return VN_ERR_INVALID_ARG;
    *count = *text_length = 0;
    try {
        auto lease = as::g_snapshots.Acquire(snapshot);
        if (!lease) return VN_ERR_INVALID_ARG;
        struct Item { const as::Session* session; float volume; BOOL mute; MixerMetadata metadata; };
        std::vector<Item> items;
        for (const auto& session : lease->sessions) {
            if (session->state == AudioSessionStateExpired || !session->volume.ptr) continue;
            const DWORD pid = session->is_system_sounds ? 0 : session->process_id;
            const auto existing = std::find_if(items.begin(), items.end(), [pid](const Item& item) {
                return (item.session->is_system_sounds ? 0 : item.session->process_id) == pid;
            });
            if (existing != items.end() && (existing->session->state == AudioSessionStateActive || session->state != AudioSessionStateActive)) continue;
            float volume = 0;
            BOOL mute = FALSE;
            if (FAILED(session->volume.ptr->GetMasterVolume(&volume)) || FAILED(session->volume.ptr->GetMute(&mute))) continue;
            if (existing != items.end()) {
                existing->session = session.get(); existing->volume = volume; existing->mute = mute;
                continue;
            }
            MixerMetadata metadata = session->is_system_sounds ? MixerMetadata{0, 0, L"System Sounds", {}} : ReadMixerMetadata(pid, session->display_name);
            if (metadata.name.empty()) continue;
            items.push_back({session.get(), std::clamp(volume, 0.0f, 1.0f), mute, std::move(metadata)});
        }
        std::sort(items.begin(), items.end(), [](const Item& a, const Item& b) {
            if (a.session->is_system_sounds != b.session->is_system_sounds) return a.session->is_system_sounds;
            return a.session->process_id < b.session->process_id;
        });
        size_t required = 0;
        for (const auto& item : items) required += item.metadata.name.size() + item.metadata.path.size() + item.session->display_name.size();
        if (required > INT32_MAX || items.size() > INT32_MAX) return VN_ERR_FAIL;
        *count = static_cast<int32_t>(items.size()); *text_length = static_cast<int32_t>(required);
        if (*count > capacity || *text_length > text_capacity) return VN_ERR_BUFFER_TOO_SMALL;
        int32_t offset = 0;
        auto copy = [&](const std::wstring& value, int32_t& start, int32_t& length) {
            start = offset; length = static_cast<int32_t>(value.size());
            if (length) std::memcpy(text + offset, value.data(), value.size() * sizeof(char16_t));
            offset += length;
        };
        for (size_t i = 0; i < items.size(); ++i) {
            const auto& item = items[i];
            auto& out = output[i]; out = {};
            out.process_id = item.session->is_system_sounds ? 0 : item.session->process_id;
            out.system_sounds = item.session->is_system_sounds ? 1 : 0;
            out.volume = std::clamp(item.volume, 0.0f, 1.0f); out.muted = item.mute ? 1 : 0;
            copy(item.metadata.name, out.name_offset, out.name_length);
            copy(item.metadata.path, out.path_offset, out.path_length);
            copy(item.session->display_name, out.raw_offset, out.raw_length);
        }
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_audio_get_sessions_batch(VnAudioSessionState* output, int32_t capacity,
    char16_t* text, int32_t text_capacity, int32_t* count, int32_t* text_length, int32_t* hresult) noexcept {
    if (!count || !text_length || !hresult || capacity < 0 || text_capacity < 0 || (capacity && !output) || (text_capacity && !text)) return VN_ERR_INVALID_ARG;
    *count = *text_length = *hresult = 0;
    int64_t snapshot = 0;
    const auto status = vn_audio_sessions_open(&snapshot, count, hresult);
    if (status != VN_OK) return status;
    struct Close { int64_t handle; ~Close() { vn_audio_sessions_close(handle); } } close{snapshot};
    return ReadSessionsBatch(snapshot, output, capacity, text, text_capacity, count, text_length);
}
