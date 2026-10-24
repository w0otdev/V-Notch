#include "../vnotch_common.h"
#include <tlhelp32.h>
#include <appmodel.h>
#include <softpub.h>
#include <wintrust.h>
#include <cstdint>

#pragma comment(lib, "wintrust.lib")

// Process, registry, file and trust queries used by the managed shell. Everything here is a
// direct Win32 call; the C# side keeps only caches and policy decisions.

namespace vnotch::services::sysinterop {

constexpr int32_t kErrNotPackaged = -5;
constexpr size_t kStackPathChars = 1024;
constexpr size_t kMaxPathChars = 32768;

#pragma pack(push, 1)
struct VnProcessEntry {
    uint32_t process_id;
    uint32_t parent_process_id;
    char16_t executable_file[260];
};
#pragma pack(pop)

struct ProcessHandle {
    HANDLE handle;
    explicit ProcessHandle(uint32_t pid) noexcept
        : handle(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)) {}
    ProcessHandle(const ProcessHandle&) = delete;
    ProcessHandle& operator=(const ProcessHandle&) = delete;
    ~ProcessHandle() {
        if (handle != nullptr) CloseHandle(handle);
    }
    explicit operator bool() const noexcept { return handle != nullptr; }
};

bool OrdinalEqualsIgnoreCase(std::u16string_view a, std::u16string_view b) noexcept {
    if (a.size() != b.size()) return false;
    if (a.empty()) return true;
    return CompareStringOrdinal(reinterpret_cast<const wchar_t*>(a.data()), static_cast<int>(a.size()),
                                reinterpret_cast<const wchar_t*>(b.data()), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}

// Image path of the process, compared with expected. Falls back to a MAX_PATH-extended buffer on demand.
bool ImagePathEquals(HANDLE process, std::u16string_view expected) noexcept {
    wchar_t stack_buffer[kStackPathChars];
    DWORD length = static_cast<DWORD>(std::size(stack_buffer));
    if (QueryFullProcessImageNameW(process, 0, stack_buffer, &length))
        return OrdinalEqualsIgnoreCase(std::u16string_view(reinterpret_cast<const char16_t*>(stack_buffer), length), expected);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) return false;

    std::vector<wchar_t> heap_buffer;
    heap_buffer.resize(kMaxPathChars);
    length = static_cast<DWORD>(heap_buffer.size());
    if (!QueryFullProcessImageNameW(process, 0, heap_buffer.data(), &length)) return false;
    return OrdinalEqualsIgnoreCase(std::u16string_view(reinterpret_cast<const char16_t*>(heap_buffer.data()), length), expected);
}

} // namespace vnotch::services::sysinterop

namespace sys = vnotch::services::sysinterop;

extern "C" {

// ---------------------------------------------------------------- processes

// Fills buffer with up to capacity entries; out_total is the full process count so callers can retry larger.
VN_EXPORT int32_t __stdcall vn_process_snapshot(
    sys::VnProcessEntry* buffer, int32_t capacity, int32_t* out_written, int32_t* out_total) noexcept {
    if (!out_written || !out_total || capacity < 0 || (capacity > 0 && !buffer)) return VN_ERR_INVALID_ARG;
    *out_written = 0;
    *out_total = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return VN_ERR_FAIL;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (entry.th32ProcessID == 0) continue;
            if (*out_written < capacity) {
                sys::VnProcessEntry& out = buffer[(*out_written)++];
                out.process_id = entry.th32ProcessID;
                out.parent_process_id = entry.th32ParentProcessID;
                std::memcpy(out.executable_file, entry.szExeFile, sizeof(out.executable_file));
            }
            ++*out_total;
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_process_image_path_equals(
    uint32_t process_id, const char16_t* expected, int32_t expected_length, uint8_t* out_equal) noexcept {
    if (!expected || expected_length < 0 || !out_equal) return VN_ERR_INVALID_ARG;
    *out_equal = 0;
    sys::ProcessHandle process(process_id);
    if (!process) return VN_ERR_FAIL;
    *out_equal = sys::ImagePathEquals(process.handle, std::u16string_view(expected, static_cast<size_t>(expected_length))) ? 1 : 0;
    return VN_OK;
}

// Package family name of the process. VN_ERR_FAIL = cannot open, -5 = not packaged,
// VN_ERR_BUFFER_TOO_SMALL = out_length holds the required size (including the terminator).
VN_EXPORT int32_t __stdcall vn_process_package_family_name(
    uint32_t process_id, char16_t* buffer, int32_t capacity, int32_t* out_length) noexcept {
    if (!buffer || capacity <= 0 || !out_length) return VN_ERR_INVALID_ARG;
    *out_length = 0;
    sys::ProcessHandle process(process_id);
    if (!process) return VN_ERR_FAIL;
    UINT32 length = static_cast<UINT32>(capacity);
    const LONG result = GetPackageFamilyName(process.handle, &length, reinterpret_cast<PWSTR>(buffer));
    if (result == ERROR_SUCCESS && length > 1) {
        *out_length = static_cast<int32_t>(length - 1);
        return VN_OK;
    }
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        *out_length = static_cast<int32_t>(length);
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return sys::kErrNotPackaged;
}

// Compares the process application user model id (case-insensitive) with expected.
VN_EXPORT int32_t __stdcall vn_process_app_user_model_id_equals(
    int64_t process_handle, const char16_t* expected, int32_t expected_length, uint8_t* out_equal) noexcept {
    if (!expected || expected_length < 0 || !out_equal) return VN_ERR_INVALID_ARG;
    *out_equal = 0;
    HANDLE process = reinterpret_cast<HANDLE>(static_cast<intptr_t>(process_handle));
    UINT32 length = 0;
    if (GetApplicationUserModelId(process, &length, nullptr) != ERROR_INSUFFICIENT_BUFFER ||
        length == 0 || length > sys::kMaxPathChars)
        return VN_OK;
    std::vector<wchar_t> id(length);
    if (GetApplicationUserModelId(process, &length, id.data()) != ERROR_SUCCESS || length == 0) return VN_OK;
    *out_equal = sys::OrdinalEqualsIgnoreCase(
        std::u16string_view(reinterpret_cast<const char16_t*>(id.data()), length - 1),
        std::u16string_view(expected, static_cast<size_t>(expected_length))) ? 1 : 0;
    return VN_OK;
}

// ---------------------------------------------------------------- registry / files / trust

// Arms an asynchronous change notification on an open key, signalled through event_handle.
VN_EXPORT int32_t __stdcall vn_registry_notify_change(
    int64_t key_handle, uint8_t watch_subtree, uint32_t filter, int64_t event_handle, int32_t* out_status) noexcept {
    const LSTATUS status = RegNotifyChangeKeyValue(
        reinterpret_cast<HKEY>(static_cast<intptr_t>(key_handle)), watch_subtree != 0, filter,
        reinterpret_cast<HANDLE>(static_cast<intptr_t>(event_handle)), TRUE);
    if (out_status) *out_status = static_cast<int32_t>(status);
    return status == ERROR_SUCCESS ? VN_OK : VN_ERR_FAIL;
}

// CREATE_NEW read handle that deletes the file when the last handle closes. Caller owns the handle.
VN_EXPORT int32_t __stdcall vn_file_create_delete_on_close(
    const char16_t* path, int32_t path_length, int64_t* out_handle, int32_t* out_last_error) noexcept {
    if (!path || path_length <= 0 || path_length >= 32768 || !out_handle) return VN_ERR_INVALID_ARG;
    std::wstring wide(reinterpret_cast<const wchar_t*>(path), static_cast<size_t>(path_length));
    SetLastError(0);
    HANDLE handle = CreateFileW(wide.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                nullptr, CREATE_NEW, FILE_FLAG_DELETE_ON_CLOSE | FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        *out_handle = 0;
        if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
        return VN_ERR_FAIL;
    }
    *out_handle = static_cast<int64_t>(reinterpret_cast<intptr_t>(handle));
    return VN_OK;
}

// WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2) for a file, no UI and no revocation check.
VN_EXPORT int32_t __stdcall vn_update_verify_authenticode(
    const char16_t* path, int32_t path_length, uint8_t* out_trusted) noexcept {
    if (!path || path_length <= 0 || path_length >= 32768 || !out_trusted) return VN_ERR_INVALID_ARG;
    *out_trusted = 0;
    std::wstring wide(reinterpret_cast<const wchar_t*>(path), static_cast<size_t>(path_length));
    WINTRUST_FILE_INFO file{};
    file.cbStruct = sizeof(file);
    file.pcwszFilePath = wide.c_str();
    WINTRUST_DATA data{};
    data.cbStruct = sizeof(data);
    data.dwUIChoice = WTD_UI_NONE;
    data.fdwRevocationChecks = WTD_REVOKE_NONE;
    data.dwUnionChoice = WTD_CHOICE_FILE;
    data.pFile = &file;
    data.dwStateAction = WTD_STATEACTION_IGNORE;
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    *out_trusted = WinVerifyTrust(nullptr, &action, &data) == 0 ? 1 : 0;
    return VN_OK;
}

// ---------------------------------------------------------------- window messaging

VN_EXPORT int32_t __stdcall vn_win32_find_window(
    const char16_t* class_name, int32_t class_length, int64_t* out_hwnd) noexcept {
    if (!class_name || class_length <= 0 || class_length >= 256 || !out_hwnd) return VN_ERR_INVALID_ARG;
    wchar_t name[256]{};
    std::copy_n(reinterpret_cast<const wchar_t*>(class_name), class_length, name);
    *out_hwnd = static_cast<int64_t>(reinterpret_cast<intptr_t>(FindWindowW(name, nullptr)));
    return VN_OK;
}

// SendMessageTimeoutW. VN_OK when the message was delivered; out_result is the window procedure result.
VN_EXPORT int32_t __stdcall vn_win32_send_message_timeout(
    int64_t hwnd, uint32_t message, int64_t wparam, int64_t lparam, uint32_t flags, uint32_t timeout_ms,
    int64_t* out_result, int32_t* out_last_error) noexcept {
    if (!out_result) return VN_ERR_INVALID_ARG;
    DWORD_PTR result = 0;
    SetLastError(0);
    const LRESULT sent = SendMessageTimeoutW(
        reinterpret_cast<HWND>(static_cast<intptr_t>(hwnd)), message,
        static_cast<WPARAM>(wparam), static_cast<LPARAM>(lparam), flags, timeout_ms, &result);
    *out_result = static_cast<int64_t>(result);
    if (sent != 0) return VN_OK;
    if (out_last_error) *out_last_error = static_cast<int32_t>(GetLastError());
    return VN_ERR_FAIL;
}

} // extern "C"
