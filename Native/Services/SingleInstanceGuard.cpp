#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class SingleInstanceGuard final {
public:
    static int32_t FormatUserMutexName(
        std::u16string_view sid,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t* out_len) noexcept {
        if (!out_buf || out_cap <= 0 || !out_len) return VN_E_INVALID_ARG;
        static constexpr std::u16string_view prefix = u"Local\\VNotch_SingleInstance_";
        const size_t total = prefix.size() + sid.size();
        if (static_cast<int32_t>(total) > out_cap) {
            *out_len = 0;
            return VN_E_INVALID_ARG;
        }
        size_t pos = 0;
        for (char16_t ch : prefix) out_buf[pos++] = ch;
        for (char16_t ch : sid) out_buf[pos++] = ch;
        *out_len = static_cast<int32_t>(pos);
        return VN_OK;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_single_instance_guard_format_mutex_name(
    const char16_t* sid_ptr,
    int32_t sid_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!sid_ptr || sid_len <= 0) return VN_E_INVALID_ARG;
    return vnotch::services::SingleInstanceGuard::FormatUserMutexName(
        std::u16string_view(sid_ptr, static_cast<size_t>(sid_len)),
        out_buf, out_cap, out_len);
}

} // extern "C"
