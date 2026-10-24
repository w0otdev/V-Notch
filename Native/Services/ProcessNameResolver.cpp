#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace vnotch::services {

class ProcessNameResolver final {
public:
    static bool TryGetNameAndExecutable(uint32_t process_id, std::u16string* out_name, std::u16string* out_exec) {
        out_name->clear();
        out_exec->clear();
        if (process_id == 0) return false;

        HANDLE raw_process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
        if (raw_process == nullptr) return false;

        struct HandleGuard {
            HANDLE h;
            ~HandleGuard() noexcept {
                if (h != nullptr && h != INVALID_HANDLE_VALUE) {
                    CloseHandle(h);
                }
            }
        } guard{raw_process};

        wchar_t stack_buf[1024];
        DWORD size = 1024;
        if (QueryFullProcessImageNameW(guard.h, 0, stack_buf, &size)) {
            ExtractNameAndExec(std::u16string_view(reinterpret_cast<const char16_t*>(stack_buf), size), out_name, out_exec);
            return true;
        }

        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
            return false;
        }

        std::vector<wchar_t> heap_buf(32768);
        size = 32768;
        bool ok = QueryFullProcessImageNameW(guard.h, 0, heap_buf.data(), &size) != FALSE;
        if (!ok) return false;

        ExtractNameAndExec(std::u16string_view(reinterpret_cast<const char16_t*>(heap_buf.data()), size), out_name, out_exec);
        return true;
    }

private:
    static void ExtractNameAndExec(std::u16string_view full_path, std::u16string* out_name, std::u16string* out_exec) {
        *out_exec = std::u16string(full_path);

        size_t last_sep = full_path.find_last_of(u"\\/:");
        std::u16string_view file_name = (last_sep == std::u16string_view::npos)
            ? full_path
            : full_path.substr(last_sep + 1);

        size_t last_dot = file_name.find_last_of(u'.');
        std::u16string_view stem = (last_dot == std::u16string_view::npos)
            ? file_name
            : file_name.substr(0, last_dot);

        *out_name = std::u16string(stem);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_process_resolver_get_name_and_exec(
    uint32_t process_id,
    char16_t* name_buf, int32_t name_cap, int32_t* out_name_len,
    char16_t* exec_buf, int32_t exec_cap, int32_t* out_exec_len) noexcept
{
    if (!out_name_len || !out_exec_len) return VN_ERR_INVALID_ARG;
    *out_name_len = 0;
    *out_exec_len = 0;
    try {
        std::u16string name, exec;
        if (!vnotch::services::ProcessNameResolver::TryGetNameAndExecutable(process_id, &name, &exec)) {
            return VN_OK;
        }
        int32_t n_len = static_cast<int32_t>(name.size());
        int32_t e_len = static_cast<int32_t>(exec.size());
        *out_name_len = n_len;
        *out_exec_len = e_len;
        if ((n_len > 0 && (!name_buf || name_cap < n_len)) ||
            (e_len > 0 && (!exec_buf || exec_cap < e_len))) {
            return VN_ERR_BUFFER_TOO_SMALL;
        }
        if (n_len > 0) std::memcpy(name_buf, name.data(), static_cast<size_t>(n_len) * sizeof(char16_t));
        if (e_len > 0) std::memcpy(exec_buf, exec.data(), static_cast<size_t>(e_len) * sizeof(char16_t));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
