#include "../vnotch_common.h"
#include <algorithm>
#include <string_view>

namespace vnotch::services {

class UpdateSecurityPolicy final {
public:
    static constexpr int32_t EvaluateAllowlistState(int32_t publisher_count, int32_t thumbprint_count) noexcept {
        if (publisher_count <= 0 && thumbprint_count <= 0) {
            return 0; // No additional policy configured
        }
        if (publisher_count <= 0 || thumbprint_count <= 0) {
            return 1; // Incomplete allowlist
        }
        return 2; // Requires verification
    }

    static int32_t NormalizeThumbprint(std::u16string_view value, char16_t* out_buf, int32_t out_cap) noexcept {
        int32_t written = 0;
        for (char16_t c : value) {
            if (c == u' ') continue;
            if (written >= out_cap) break;
            if (c >= u'a' && c <= u'z') {
                out_buf[written++] = static_cast<char16_t>(c - u'a' + u'A');
            } else {
                out_buf[written++] = c;
            }
        }
        return written;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_update_security_policy_evaluate_allowlist(
    int32_t publisher_count,
    int32_t thumbprint_count,
    int32_t* out_state) noexcept {
    if (!out_state) return VN_E_INVALID_ARG;
    *out_state = vnotch::services::UpdateSecurityPolicy::EvaluateAllowlistState(publisher_count, thumbprint_count);
    return VN_OK;
}

VN_EXPORT int32_t vn_update_security_policy_normalize_thumbprint(
    const char16_t* value,
    int32_t value_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap < 0 || !out_len || value_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(value ? value : u"", static_cast<size_t>(value_len));
    *out_len = vnotch::services::UpdateSecurityPolicy::NormalizeThumbprint(sv, out_buf, out_cap);
    return VN_OK;
}

} // extern "C"
