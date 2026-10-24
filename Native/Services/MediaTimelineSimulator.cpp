#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class MediaTimelineSimulator final {
public:
    static constexpr bool IsAtEndStuck(double progress, int64_t now_utc_ticks, int64_t last_metadata_change_ticks, int64_t threshold_ticks) noexcept {
        return progress > 0.98 && (now_utc_ticks - last_metadata_change_ticks) > threshold_ticks;
    }

    static constexpr int64_t ComputeSimulatedPositionTicks(
        int64_t sim_base_position_ticks,
        int64_t sim_base_wall_utc_ticks,
        int64_t now_utc_ticks,
        double playback_rate,
        int64_t duration_ticks,
        bool at_end_stuck) noexcept
    {
        double elapsed_seconds = static_cast<double>(now_utc_ticks - sim_base_wall_utc_ticks) / static_cast<double>(vnotch::TICKS_PER_SECOND);
        int64_t sim_ticks = sim_base_position_ticks + static_cast<int64_t>(elapsed_seconds * playback_rate * static_cast<double>(vnotch::TICKS_PER_SECOND));
        if (!at_end_stuck && duration_ticks > 0 && sim_ticks > duration_ticks) {
            sim_ticks = duration_ticks;
        }
        return sim_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_timelinesim_is_at_end_stuck(
    double progress, int64_t now_utc_ticks, int64_t last_metadata_change_ticks, int64_t threshold_ticks,
    uint8_t* out_stuck) noexcept
{
    if (!out_stuck) return VN_ERR_INVALID_ARG;
    *out_stuck = vnotch::services::MediaTimelineSimulator::IsAtEndStuck(
        progress, now_utc_ticks, last_metadata_change_ticks, threshold_ticks) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_timelinesim_compute_position_ticks(
    int64_t sim_base_position_ticks,
    int64_t sim_base_wall_utc_ticks,
    int64_t now_utc_ticks,
    double playback_rate,
    int64_t duration_ticks,
    uint8_t at_end_stuck,
    int64_t* out_sim_ticks) noexcept
{
    if (!out_sim_ticks) return VN_ERR_INVALID_ARG;
    *out_sim_ticks = vnotch::services::MediaTimelineSimulator::ComputeSimulatedPositionTicks(
        sim_base_position_ticks, sim_base_wall_utc_ticks, now_utc_ticks, playback_rate, duration_ticks, at_end_stuck != 0);
    return VN_OK;
}

} // extern "C"
