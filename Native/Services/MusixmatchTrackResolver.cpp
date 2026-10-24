#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <string_view>

namespace vnotch::services {

class MusixmatchTrackResolver final {
public:
    static int32_t ComputeDurationSeconds(double total_seconds) noexcept {
        if (!std::isfinite(total_seconds) || total_seconds <= 1.0) return 1;
        const double fl = std::floor(total_seconds);
        const double diff = total_seconds - fl;
        int32_t rounded = 1;
        if (diff > 0.5) {
            rounded = static_cast<int32_t>(fl + 1.0);
        } else if (diff < 0.5) {
            rounded = static_cast<int32_t>(fl);
        } else {
            const int64_t fl_int = static_cast<int64_t>(fl);
            rounded = (fl_int % 2 == 0) ? static_cast<int32_t>(fl_int) : static_cast<int32_t>(fl_int + 1);
        }
        return std::max(1, rounded);
    }

    static constexpr bool IsTokenValid(std::u16string_view token, int64_t expires_at_utc_ticks, int64_t now_utc_ticks) noexcept {
        return !token.empty() && expires_at_utc_ticks > now_utc_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_musixmatch_resolver_compute_duration_seconds(
    double total_seconds,
    int32_t* out_seconds) noexcept {
    if (!out_seconds) return VN_E_INVALID_ARG;
    *out_seconds = vnotch::services::MusixmatchTrackResolver::ComputeDurationSeconds(total_seconds);
    return VN_OK;
}

VN_EXPORT int32_t vn_musixmatch_resolver_is_token_valid(
    const char16_t* token,
    int32_t token_len,
    int64_t expires_at_utc_ticks,
    int64_t now_utc_ticks,
    uint8_t* out_valid) noexcept {
    if (!out_valid || token_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(token ? token : u"", static_cast<size_t>(token_len));
    *out_valid = vnotch::services::MusixmatchTrackResolver::IsTokenValid(sv, expires_at_utc_ticks, now_utc_ticks) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
