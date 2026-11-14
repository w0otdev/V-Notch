#include "vnotch_common.h"
#include "vnotch_com.h"
#include <tlhelp32.h>
#include <shlobj.h>
#include <array>

#pragma comment(lib, "shell32.lib")

VN_EXPORT int32_t __stdcall vn_ffmpeg_read_process_command_line(uint32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_ffmpeg_is_screen_capture_command(const char16_t*, int32_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_bandicam_is_supported_output_path(const char16_t*, int32_t, uint8_t*) noexcept;

namespace {
uint64_t file_time(FILETIME value) noexcept { return (uint64_t{value.dwHighDateTime} << 32) | value.dwLowDateTime; }
struct Recorder { DWORD pid; uint64_t started; int kind; std::wstring executable; };

bool is_locked_for_write(const wchar_t* path, DWORD flags = FILE_ATTRIBUTE_NORMAL) noexcept {
    if (!path || !*path) return false;
    HANDLE handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, flags, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return GetLastError() == ERROR_SHARING_VIOLATION;
    }
    CloseHandle(handle);
    return false;
}

bool resolve_bandicam_recording_path(const wchar_t* path, std::wstring& canonical) {
    canonical.clear();
    if (!path || !*path) return false;
    size_t length = 0;
    while (length < 32768 && path[length] != L'\0') ++length;
    if (length == 0 || length >= 32768) return false;

    uint8_t supported = 0;
    if (vn_bandicam_is_supported_output_path(reinterpret_cast<const char16_t*>(path), static_cast<int32_t>(length), &supported) != VN_OK || !supported) {
        return false;
    }

    const wchar_t drive_root[4]{path[0], L':', L'\\', L'\0'};
    const UINT drive_type = GetDriveTypeW(drive_root);
    if (drive_type != DRIVE_FIXED && drive_type != DRIVE_REMOVABLE && drive_type != DRIVE_RAMDISK) {
        return false;
    }

    std::array<wchar_t, 512> buffer{};
    wchar_t* file_part = nullptr;
    DWORD written = GetFullPathNameW(path, static_cast<DWORD>(buffer.size()), buffer.data(), &file_part);
    if (written == 0 || written >= 32768) return false;
    if (written < buffer.size()) {
        if (!file_part || !*file_part) return false;
        canonical.assign(buffer.data(), written);
    } else {
        std::vector<wchar_t> large(static_cast<size_t>(written) + 1);
        written = GetFullPathNameW(path, static_cast<DWORD>(large.size()), large.data(), &file_part);
        if (written == 0 || written >= large.size() || !file_part || !*file_part) return false;
        canonical.assign(large.data(), written);
    }

    if (CompareStringOrdinal(path, static_cast<int>(length), canonical.data(), static_cast<int>(canonical.size()), TRUE) != CSTR_EQUAL) {
        canonical.clear();
        return false;
    }

    const DWORD attrs = GetFileAttributesW(canonical.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES ||
        (attrs & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DEVICE | FILE_ATTRIBUTE_OFFLINE)) != 0) {
        canonical.clear();
        return false;
    }
    return true;
}

struct ObsLog {
    uint64_t file_id{}, position{}, seen{};
    std::wstring path;
    std::string partial;
    bool recording{}, streaming{}, replay{};
    void feed(std::string_view bytes) {
        // Activity markers are ASCII. Preserve raw partial bytes across UTF-8 write boundaries.
        partial.append(bytes);
        size_t begin = 0, end;
        while ((end = partial.find('\n', begin)) != partial.npos) {
            const std::string_view line(partial.data() + begin, end - begin);
            auto apply = [&](std::string_view start, std::string_view stop, bool& active) {
                const auto start_pos = line.find(start);
                const auto stop_pos = line.find(stop);
                if (start_pos != line.npos && (stop_pos == line.npos || start_pos > stop_pos)) active = true;
                else if (stop_pos != line.npos) active = false;
            };
            apply("==== Recording Start ===", "==== Recording Stop ===", recording);
            apply("==== Streaming Start ===", "==== Streaming Stop ===", streaming);
            apply("==== Replay Buffer Start ===", "==== Replay Buffer Stop ===", replay);
            begin = end + 1;
        }
        partial.erase(0, begin);
        if (partial.size() > 4096) partial.clear();
    }
    bool read(bool* success = nullptr) {
        if (success) *success = false;
        vnotch::UniqueHandle file(CreateFileW(path.c_str(), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (!file.valid()) return false;
        BY_HANDLE_FILE_INFORMATION info{};
        LARGE_INTEGER size{};
        if (!GetFileInformationByHandle(file.get(), &info) || !GetFileSizeEx(file.get(), &size) || size.QuadPart < 0) return false;
        const uint64_t id = (uint64_t{info.nFileIndexHigh} << 32) | info.nFileIndexLow;
        const auto length = static_cast<uint64_t>(size.QuadPart);
        if (id != file_id || length < position) {
            recording = streaming = replay = false;
            partial.clear();
            position = 0;
            file_id = id;
        }
        if (position < length) {
            LARGE_INTEGER offset{}; offset.QuadPart = static_cast<LONGLONG>(position);
            if (!SetFilePointerEx(file.get(), offset, nullptr, FILE_BEGIN)) return false;
            char chunk[32 * 1024];
            while (position < length) {
                DWORD read_bytes = 0;
                const auto amount = static_cast<DWORD>(std::min<uint64_t>(sizeof(chunk), length - position));
                if (!ReadFile(file.get(), chunk, amount, &read_bytes, nullptr)) return false;
                if (read_bytes == 0) break;
                position += read_bytes;
                feed(std::string_view(chunk, read_bytes));
            }
        }
        if (success) *success = true;
        return recording || streaming || replay;
    }
};
struct FfmpegCache { DWORD pid; uint64_t started, seen; bool capture; };
SRWLOCK poll_lock = SRWLOCK_INIT;
std::vector<Recorder> recorders;
std::vector<ObsLog> logs;
std::vector<FfmpegCache> commands;
uint64_t generation = 0;

void add_recorder(DWORD pid, const wchar_t* name) {
    const int kind = !_wcsicmp(name, L"bdcam.exe") ? 1 :
        (!_wcsicmp(name, L"obs64.exe") || !_wcsicmp(name, L"obs32.exe")) ? 2 : !_wcsicmp(name, L"ffmpeg.exe") ? 3 : 0;
    if (!kind) return;
    vnotch::UniqueHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    FILETIME created{}, exited{}, kernel{}, user{};
    DWORD exit_code = 0;
    if (!process.valid() || !GetProcessTimes(process.get(), &created, &exited, &kernel, &user) ||
        !GetExitCodeProcess(process.get(), &exit_code) || exit_code != STILL_ACTIVE) return;
    wchar_t path[32768]; DWORD chars = ARRAYSIZE(path);
    std::wstring executable;
    if (QueryFullProcessImageNameW(process.get(), 0, path, &chars)) executable.assign(path, chars);
    recorders.push_back({pid, file_time(created), kind, std::move(executable)});
}

bool scan_obs_folder(const std::wstring& folder, const Recorder& recorder) {
    struct Candidate { uint64_t created{}; uint64_t written{}; std::wstring path; bool locked{}; };
    std::array<Candidate, 4> newest{};
    constexpr uint64_t kCreateSlack = 100'000ULL;    // 10ms FAT creation-time slack
    constexpr uint64_t kWriteSlack  = 20'000'000ULL; // 2s FAT/exFAT write-time slack
    const uint64_t min_created = recorder.started > kCreateSlack ? recorder.started - kCreateSlack : 0;
    const uint64_t min_written = recorder.started > kWriteSlack  ? recorder.started - kWriteSlack  : 0;

    WIN32_FIND_DATAW data{};
    const auto pattern = folder + L"\\*.txt";
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return false;
    struct FindGuard { HANDLE handle; ~FindGuard() { FindClose(handle); } } guard{find};
    do {
        if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        const uint64_t created = file_time(data.ftCreationTime);
        const uint64_t written = file_time(data.ftLastWriteTime);
        if (written < min_written) continue;
        for (size_t i = 0; i < newest.size(); ++i) {
            if (newest[i].path.empty() || written > newest[i].written) {
                for (size_t j = newest.size() - 1; j > i; --j) newest[j] = std::move(newest[j - 1]);
                newest[i] = {created, written, folder + L"\\" + data.cFileName, false};
                break;
            }
        }
    } while (FindNextFileW(find, &data));

    bool any_locked = false;
    for (auto& candidate : newest) {
        if (candidate.path.empty()) continue;
        candidate.locked = is_locked_for_write(candidate.path.c_str());
        any_locked |= candidate.locked;
    }

    bool active = false;
    bool took_unlocked_fallback = false;
    for (const auto& file : newest) {
        if (file.path.empty()) continue;
        if (any_locked) {
            if (!file.locked) continue;
        } else {
            if (took_unlocked_fallback) continue;
            if (file.created != 0 && file.created < min_created) continue;
            took_unlocked_fallback = true;
        }
        auto found = std::find_if(logs.begin(), logs.end(), [&](const auto& log) {
            return log.path == file.path;
        });
        if (found == logs.end()) {
            if (logs.size() >= 128) continue;
            logs.emplace_back();
            found = logs.end() - 1;
            found->path = file.path;
        }
        found->seen = generation;
        active |= found->read();
    }
    return active;
}

bool poll(const VnPrivacyProcessEntry* entries, int32_t count, bool capture) {
    ++generation; recorders.clear();
    if (capture) {
        vnotch::UniqueHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        if (!snapshot.valid()) return false;
        PROCESSENTRY32W process{}; process.dwSize = sizeof(process);
        if (Process32FirstW(snapshot.get(), &process)) do { add_recorder(process.th32ProcessID, process.szExeFile); }
            while (Process32NextW(snapshot.get(), &process));
    } else {
        for (int32_t i = 0; i < count; ++i) {
            wchar_t name[261]{};
            std::memcpy(name, entries[i].executable_file, sizeof(entries[i].executable_file));
            add_recorder(entries[i].process_id, name);
        }
    }
    bool active = false;
    FILETIME current{}; GetSystemTimeAsFileTime(&current);
    for (const auto& recorder : recorders) {
        try {
            if (recorder.kind == 1) {
                wchar_t path[32768]{}; DWORD bytes = sizeof(path);
                if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\BANDISOFT\\BANDICAM\\OPTION", L"sLatestRecordingFile",
                    RRF_RT_REG_SZ, nullptr, path, &bytes) == ERROR_SUCCESS) {
                    std::wstring canonical;
                    if (resolve_bandicam_recording_path(path, canonical)) {
                        active |= is_locked_for_write(canonical.c_str(), FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT);
                    }
                }
            } else if (recorder.kind == 2) {
                wchar_t roaming[32768];
                DWORD chars = GetEnvironmentVariableW(L"APPDATA", roaming, ARRAYSIZE(roaming));
                if (chars && chars < ARRAYSIZE(roaming)) active |= scan_obs_folder(std::wstring(roaming, chars) + L"\\obs-studio\\logs", recorder);
                auto root = recorder.executable;
                for (int i = 0; i < 3 && !root.empty(); ++i) {
                    const auto slash = root.find_last_of(L"\\/");
                    if (slash == root.npos) root.clear(); else root.resize(slash);
                }
                if (!root.empty()) active |= scan_obs_folder(root + L"\\config\\obs-studio\\logs", recorder);
            } else if (file_time(current) >= recorder.started + 20000000ULL) {
                auto cached = std::find_if(commands.begin(), commands.end(), [&](const auto& item) {
                    return item.pid == recorder.pid && item.started == recorder.started;
                });
                if (cached == commands.end()) {
                    char16_t command[32768]; int32_t length = 0; uint8_t capture_command = 0;
                    if (vn_ffmpeg_read_process_command_line(recorder.pid, command, ARRAYSIZE(command), &length) != VN_OK) continue;
                    vn_ffmpeg_is_screen_capture_command(command, length, &capture_command);
                    commands.push_back({recorder.pid, recorder.started, generation, capture_command != 0});
                    cached = commands.end() - 1;
                }
                cached->seen = generation; active |= cached->capture;
            }
        } catch (...) { /* Recorder failures are independent. */ }
    }
    std::erase_if(logs, [](const auto& item) { return item.seen != generation; });
    std::erase_if(commands, [](const auto& item) { return item.seen != generation; });
    return active;
}
}

VN_EXPORT int32_t __stdcall vn_privacy_poll_recorders(const VnPrivacyProcessEntry* entries, int32_t count, uint8_t capture, uint8_t* active) noexcept {
    if (!active || count < 0 || (count && !entries)) return VN_ERR_INVALID_ARG;
    *active = 0;
    try {
        vnotch::com::Guard guard(&poll_lock);
        *active = poll(entries, count, capture != 0) ? 1 : 0;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
