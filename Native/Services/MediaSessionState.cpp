#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnTimelineAdvanceState {
    int64_t last_timeline_position_ticks;
    int64_t last_timeline_sample_utc_ticks;
    int64_t last_position_advance_utc_ticks;
    uint8_t has_timeline_position;
    uint8_t has_timeline_sample;
    uint8_t has_position_advance;
};
#pragma pack(pop)

class MediaSessionState final {
public:
    static constexpr int64_t kMinAdvanceSampleIntervalTicks = 350LL * vnotch::TICKS_PER_MILLISECOND;

    // 0 = Indeterminate, 1 = Advanced, 2 = Stalled
    static constexpr int32_t RecordTimelinePosition(
        VnTimelineAdvanceState& state, int64_t position_ticks, int64_t now_utc_ticks) noexcept
    {
        if (!state.has_timeline_sample || !state.has_timeline_position) {
            state.last_timeline_position_ticks = position_ticks;
            state.last_timeline_sample_utc_ticks = now_utc_ticks;
            state.has_timeline_position = 1;
            state.has_timeline_sample = 1;
            return 0;
        }

        int64_t elapsed_ticks = now_utc_ticks - state.last_timeline_sample_utc_ticks;
        if (elapsed_ticks < kMinAdvanceSampleIntervalTicks) {
            return 0;
        }

        int64_t delta_ticks = position_ticks - state.last_timeline_position_ticks;
        state.last_timeline_position_ticks = position_ticks;
        state.last_timeline_sample_utc_ticks = now_utc_ticks;

        if (delta_ticks >= static_cast<int64_t>(static_cast<double>(elapsed_ticks) * 0.4)) {
            state.last_position_advance_utc_ticks = now_utc_ticks;
            state.has_position_advance = 1;
            return 1;
        }

        return 2;
    }

    static constexpr bool IsRecentlyAdvancing(
        const VnTimelineAdvanceState& state, int64_t now_utc_ticks, int64_t window_ticks) noexcept
    {
        if (!state.has_position_advance) return false;
        int64_t age_ticks = now_utc_ticks - state.last_position_advance_utc_ticks;
        return age_ticks >= 0 && age_ticks <= window_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_mediasess_record_timeline_position(
    vnotch::services::VnTimelineAdvanceState* state,
    int64_t position_ticks, int64_t now_utc_ticks,
    int32_t* out_result) noexcept
{
    if (!state || !out_result) return VN_ERR_INVALID_ARG;
    *out_result = vnotch::services::MediaSessionState::RecordTimelinePosition(*state, position_ticks, now_utc_ticks);
    return VN_OK;
}

VN_EXPORT int32_t vn_mediasess_is_recently_advancing(
    const vnotch::services::VnTimelineAdvanceState* state,
    int64_t now_utc_ticks, int64_t window_ticks,
    uint8_t* out_advancing) noexcept
{
    if (!state || !out_advancing) return VN_ERR_INVALID_ARG;
    *out_advancing = vnotch::services::MediaSessionState::IsRecentlyAdvancing(*state, now_utc_ticks, window_ticks) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
