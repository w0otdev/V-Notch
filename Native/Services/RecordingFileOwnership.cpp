#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <restartmanager.h>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#pragma comment(lib, "rstrtmgr.lib")

namespace vnotch::services {

class RecordingFileOwnership final {
public:
    static constexpr bool MatchesRecorder(
        uint32_t owner_pid,
        int32_t high_date_time,
        uint32_t low_date_time,
        int32_t process_id,
        int64_t start_file_time) noexcept
    {
        int64_t owner_time = (static_cast<int64_t>(high_date_time) << 32) | static_cast<uint32_t>(low_date_time);
        return owner_pid == static_cast<uint32_t>(process_id) && owner_time == start_file_time;
    }

    static bool IsFileOwnedByProcess(std::u16string_view path, int32_t process_id, int64_t start_file_time) {
        if (path.empty()) return false;

        DWORD session = 0;
        wchar_t session_key[CCH_RM_SESSION_KEY + 1]{};
        if (RmStartSession(&session, 0, session_key) != ERROR_SUCCESS) {
            return false;
        }

        std::wstring path_w(reinterpret_cast<const wchar_t*>(path.data()), path.size());
        LPCWSTR resources[1] = { path_w.c_str() };
        if (RmRegisterResources(session, 1, resources, 0, nullptr, 0, nullptr) != ERROR_SUCCESS) {
            RmEndSession(session);
            return false;
        }

        UINT needed = 0;
        UINT count = 16;
        DWORD reason = RmRebootReasonNone;
        std::vector<RM_PROCESS_INFO> owners(count);
        DWORD result = RmGetList(session, &needed, &count, owners.data(), &reason);
        if (result == ERROR_MORE_DATA && needed <= 128) {
            count = needed;
            owners.resize(count);
            result = RmGetList(session, &needed, &count, owners.data(), &reason);
        }
        RmEndSession(session);

        if (result != ERROR_SUCCESS) {
            return false;
        }

        for (UINT i = 0; i < count; i++) {
            const auto& proc = owners[i].Process;
            if (MatchesRecorder(
                    proc.dwProcessId,
                    static_cast<int32_t>(proc.ProcessStartTime.dwHighDateTime),
                    proc.ProcessStartTime.dwLowDateTime,
                    process_id,
                    start_file_time)) {
                return true;
            }
        }
        return false;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_recording_matches_recorder(
    uint32_t owner_pid,
    int32_t high_date_time,
    uint32_t low_date_time,
    int32_t process_id,
    int64_t start_file_time,
    uint8_t* out_matches) noexcept
{
    if (!out_matches) return VN_ERR_INVALID_ARG;
    *out_matches = vnotch::services::RecordingFileOwnership::MatchesRecorder(
        owner_pid, high_date_time, low_date_time, process_id, start_file_time) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_recording_is_file_owned_by_process(
    const char16_t* path_ptr, int32_t path_len,
    int32_t process_id, int64_t start_file_time,
    uint8_t* out_owned) noexcept
{
    if (!out_owned) return VN_ERR_INVALID_ARG;
    if (!path_ptr || path_len <= 0) {
        *out_owned = 0;
        return VN_OK;
    }
    try {
        *out_owned = vnotch::services::RecordingFileOwnership::IsFileOwnedByProcess(
            std::u16string_view(path_ptr, static_cast<size_t>(path_len)),
            process_id,
            start_file_time) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
