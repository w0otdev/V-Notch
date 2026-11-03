#include "vnotch_common.h"
#include "vnotch_com.h"
#include <tlhelp32.h>
#include <restartmanager.h>
#include <shlobj.h>
#include <array>

#pragma comment(lib, "rstrtmgr.lib")
#pragma comment(lib, "shell32.lib")

VN_EXPORT int32_t __stdcall vn_ffmpeg_read_process_command_line(uint32_t, char16_t*, int32_t, int32_t*) noexcept;
VN_EXPORT int32_t vn_ffmpeg_is_screen_capture_command(const char16_t*, int32_t, uint8_t*) noexcept;
VN_EXPORT int32_t vn_bandicam_is_supported_output_path(const char16_t*, int32_t, uint8_t*) noexcept;

namespace {
uint64_t file_time(FILETIME value) noexcept { return (uint64_t{value.dwHighDateTime} << 32) | value.dwLowDateTime; }
struct Recorder { DWORD pid; uint64_t started; int kind; std::wstring executable; };
struct Ownership { DWORD pid; uint64_t started; std::wstring path; ULONGLONG expires; bool owned; };
struct ObsLog {
    DWORD pid{}; uint64_t started{}, file_id{}, position{}, seen{};
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
                if (line.find(start) != line.npos) active = true;
                if (line.find(stop) != line.npos) active = false;
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
        bool skip_first = false;
        if (id != file_id || length < position || length - position > 1024 * 1024) {
            recording = streaming = replay = false;
            partial.clear();
            position = length > 1024 * 1024 ? length - 1024 * 1024 : 0;
            skip_first = position != 0;
            file_id = id;
        }
        LARGE_INTEGER offset{}; offset.QuadPart = static_cast<LONGLONG>(position);
        if (!SetFilePointerEx(file.get(), offset, nullptr, FILE_BEGIN)) return false;
        char chunk[16 * 1024];
        while (position < length) {
            DWORD read = 0;
            const auto amount = static_cast<DWORD>(std::min<uint64_t>(sizeof(chunk), length - position));
            if (!ReadFile(file.get(), chunk, amount, &read, nullptr)) return false;
            if (read == 0) break;
            position += read;
            std::string_view bytes(chunk, read);
            if (skip_first) {
                const auto newline = bytes.find('\n');
                if (newline == bytes.npos) continue;
                bytes.remove_prefix(newline + 1); skip_first = false;
            }
            feed(bytes);
        }
        if (success) *success = true;
        return recording || streaming || replay;
    }
};
struct FfmpegCache { DWORD pid; uint64_t started, seen; bool capture; };
SRWLOCK poll_lock = SRWLOCK_INIT;
std::vector<Recorder> recorders;
std::vector<Ownership> ownership;
std::vector<ObsLog> logs;
std::vector<FfmpegCache> commands;
uint64_t generation = 0;

bool owned_by(const std::wstring& path, const Recorder& recorder, ULONGLONG positive_ttl = 2000, ULONGLONG negative_ttl = 1000) {
    const auto now = GetTickCount64();
    for (const auto& cached : ownership)
        if (cached.pid == recorder.pid && cached.started == recorder.started && cached.path == path && now < cached.expires) return cached.owned;
    DWORD session = 0; wchar_t key[CCH_RM_SESSION_KEY + 1]{};
    if (RmStartSession(&session, 0, key) != ERROR_SUCCESS) return false;
    struct RmGuard { DWORD id; ~RmGuard() { RmEndSession(id); } } guard{session};
    const wchar_t* resources[]{path.c_str()};
    if (RmRegisterResources(session, 1, resources, 0, nullptr, 0, nullptr) != ERROR_SUCCESS) return false;
    std::array<RM_PROCESS_INFO, 16> stack{};
    std::vector<RM_PROCESS_INFO> extra;
    auto owners = stack.data(); UINT needed = 0, count = static_cast<UINT>(stack.size()); DWORD reason = 0;
    auto status = RmGetList(session, &needed, &count, owners, &reason);
    for (int retry = 0; status == ERROR_MORE_DATA && retry < 3 && needed <= 1024; ++retry) {
        extra.resize(needed); owners = extra.data(); count = needed;
        status = RmGetList(session, &needed, &count, owners, &reason);
    }
    if (status != ERROR_SUCCESS) return false;
    bool owned = false;
    for (UINT i = 0; i < count; ++i)
        owned |= owners[i].Process.dwProcessId == recorder.pid && file_time(owners[i].Process.ProcessStartTime) == recorder.started;
    std::erase_if(ownership, [now](const auto& item) { return now >= item.expires; });
    if (ownership.size() >= 128) ownership.erase(ownership.begin());
    ownership.push_back({recorder.pid, recorder.started, path, now + (owned ? positive_ttl : negative_ttl), owned});
    return owned;
}
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
    struct Candidate { FILETIME time{}; std::wstring path; };
    std::array<Candidate, 4> newest;
    WIN32_FIND_DATAW data{};
    const auto pattern = folder + L"\\*.txt";
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return false;
    struct FindGuard { HANDLE handle; ~FindGuard() { FindClose(handle); } } guard{find};
    do {
        if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        for (size_t i = 0; i < newest.size(); ++i) {
            if (newest[i].path.empty() || CompareFileTime(&data.ftLastWriteTime, &newest[i].time) > 0) {
                for (size_t j = newest.size() - 1; j > i; --j) newest[j] = std::move(newest[j - 1]);
                newest[i] = {data.ftLastWriteTime, folder + L"\\" + data.cFileName};
                break;
            }
        }
    } while (FindNextFileW(find, &data));
    bool active = false;
    for (const auto& file : newest) {
        if (file.path.empty() || !owned_by(file.path, recorder, 5000, 10000)) continue;
        auto found = std::find_if(logs.begin(), logs.end(), [&](const auto& log) {
            return log.pid == recorder.pid && log.started == recorder.started && log.path == file.path;
        });
        if (found == logs.end()) {
            if (logs.size() >= 128) continue;
            logs.emplace_back(); found = logs.end() - 1;
            found->pid = recorder.pid; found->started = recorder.started; found->path = file.path;
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
                    uint8_t supported = 0;
                    vn_bandicam_is_supported_output_path(reinterpret_cast<char16_t*>(path), static_cast<int32_t>(wcslen(path)), &supported);
                    if (supported && GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES) active |= owned_by(path, recorder);
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

VN_EXPORT int32_t __stdcall vn_obs_read_log(const char16_t* path, int32_t path_length, VnObsLogReadState* state,
    char16_t* partial, int32_t capacity, int32_t* written) noexcept {
    if (!path || path_length <= 0 || !state || !written || capacity < 0 || (capacity && !partial) ||
        state->partial_length < 0 || state->partial_length > sizeof(state->partial)) return VN_ERR_INVALID_ARG;
    *written = 0;
    try {
        ObsLog log;
        log.path.assign(reinterpret_cast<const wchar_t*>(path), path_length);
        log.position = state->position; log.file_id = state->file_id;
        log.recording = state->recording != 0; log.streaming = state->streaming != 0; log.replay = state->replay != 0;
        log.partial.assign(state->partial, state->partial_length);
        bool success = false;
        log.read(&success);
        if (!success) return VN_ERR_FAIL;
        size_t complete = log.partial.size();
        if (complete) {
            size_t lead = complete - 1;
            while (lead > 0 && complete - lead < 4 && (static_cast<uint8_t>(log.partial[lead]) & 0xC0) == 0x80) --lead;
            const auto ch = static_cast<uint8_t>(log.partial[lead]);
            const size_t expected = (ch & 0xE0) == 0xC0 ? 2 : (ch & 0xF0) == 0xE0 ? 3 : (ch & 0xF8) == 0xF0 ? 4 : 1;
            if (complete - lead < expected) complete = lead;
        }
        const int required = complete ? MultiByteToWideChar(CP_UTF8, 0, log.partial.data(), static_cast<int>(complete), nullptr, 0) : 0;
        *written = required;
        if (required > capacity) return VN_ERR_BUFFER_TOO_SMALL;
        if (required && MultiByteToWideChar(CP_UTF8, 0, log.partial.data(), static_cast<int>(complete), reinterpret_cast<wchar_t*>(partial), capacity) != required) return VN_ERR_FAIL;
        state->position = log.position; state->file_id = log.file_id;
        state->recording = log.recording ? 1 : 0; state->streaming = log.streaming ? 1 : 0; state->replay = log.replay ? 1 : 0;
        state->partial_length = static_cast<int32_t>(log.partial.size());
        if (!log.partial.empty()) std::memcpy(state->partial, log.partial.data(), log.partial.size());
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
