#include "../vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class MediaTransportControlService final {
public:
    static constexpr double kRestartThresholdSeconds = 3.0;
    static constexpr double kConsecutiveClickWindowSeconds = 3.0;
    static constexpr double kTicksPerSecond = 10000000.0;

    static constexpr bool ShouldRewindTrack(
        int64_t current_pos_ticks,
        int64_t start_time_ticks,
        int64_t end_time_ticks,
        bool has_timeline,
        int64_t now_utc_ticks,
        int64_t last_rewind_utc_ticks,
        std::u16string_view last_session_id,
        std::u16string_view current_session_id,
        int64_t& out_start_target_ticks,
        double& out_relative_seconds) noexcept {
        const int64_t relative_ticks = (has_timeline && current_pos_ticks >= start_time_ticks)
            ? (current_pos_ticks - start_time_ticks)
            : current_pos_ticks;
        out_relative_seconds = static_cast<double>(relative_ticks) / kTicksPerSecond;
        out_start_target_ticks = start_time_ticks > 0 ? start_time_ticks : 0;

        const double elapsed_since_rewind = static_cast<double>(now_utc_ticks - last_rewind_utc_ticks) / kTicksPerSecond;
        const bool is_consecutive = elapsed_since_rewind < kConsecutiveClickWindowSeconds &&
                                    vnotch::vn_equals_icase_ascii(last_session_id, current_session_id);

        return !is_consecutive && has_timeline && end_time_ticks > 0 && out_relative_seconds > kRestartThresholdSeconds;
    }

    static constexpr int64_t ClampSeekTargetTicks(int64_t target_ticks, int64_t end_time_ticks) noexcept {
        if (target_ticks < 0) target_ticks = 0;
        if (end_time_ticks > 0 && target_ticks > end_time_ticks) {
            target_ticks = end_time_ticks;
        }
        return target_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_transport_should_rewind_track(
    int64_t current_pos_ticks,
    int64_t start_time_ticks,
    int64_t end_time_ticks,
    uint8_t has_timeline,
    int64_t now_utc_ticks,
    int64_t last_rewind_utc_ticks,
    const char16_t* last_session_id,
    int32_t last_session_len,
    const char16_t* current_session_id,
    int32_t current_session_len,
    int64_t* out_start_target_ticks,
    double* out_relative_seconds,
    uint8_t* out_should_rewind) noexcept {
    if (!out_start_target_ticks || !out_relative_seconds || !out_should_rewind ||
        last_session_len < 0 || current_session_len < 0) {
        return VN_E_INVALID_ARG;
    }
    std::u16string_view last_sv(last_session_id ? last_session_id : u"", static_cast<size_t>(last_session_len));
    std::u16string_view cur_sv(current_session_id ? current_session_id : u"", static_cast<size_t>(current_session_len));
    *out_should_rewind = vnotch::services::MediaTransportControlService::ShouldRewindTrack(
        current_pos_ticks,
        start_time_ticks,
        end_time_ticks,
        has_timeline != 0,
        now_utc_ticks,
        last_rewind_utc_ticks,
        last_sv,
        cur_sv,
        *out_start_target_ticks,
        *out_relative_seconds) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_transport_clamp_seek_target(
    int64_t target_ticks,
    int64_t end_time_ticks,
    int64_t* out_clamped_ticks) noexcept {
    if (!out_clamped_ticks) return VN_E_INVALID_ARG;
    *out_clamped_ticks = vnotch::services::MediaTransportControlService::ClampSeekTargetTicks(target_ticks, end_time_ticks);
    return VN_OK;
}

} // extern "C"
