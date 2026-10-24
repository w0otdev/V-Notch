#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class MediaTimelineClassifier final
    {
    public:
        static int32_t vn_timeline_classifier_observe_impl(VnTimelineClassifierState* state,
    uint8_t identity_changed,
    int64_t end_time_ticks,
    int64_t seek_window_start_ticks,
    int64_t now_utc_ticks,
    uint8_t is_playing,
    uint8_t* out_is_live) noexcept
        {
    if (state == nullptr || out_is_live == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (identity_changed != 0 ||
        end_time_ticks < state->last_end_ticks ||
        (state->has_last_end_observed != 0 && now_utc_ticks < state->last_end_observed_utc_ticks))
    {
        state->last_end_ticks = 0;
        state->last_end_observed_utc_ticks = 0;
        state->has_last_end_observed = 0;
        state->growth_samples = 0;
        state->is_live = 0;
        state->was_playing = 0;
    }

    if (seek_window_start_ticks > 0)
    {
        state->is_live = 1;
    }

    bool resumed = (is_playing != 0) && (state->was_playing == 0) && (state->has_last_end_observed != 0);
    if (resumed)
    {
        state->last_end_ticks = end_time_ticks;
        state->last_end_observed_utc_ticks = now_utc_ticks;
        state->has_last_end_observed = 1;
    }
    else if (state->has_last_end_observed != 0)
    {
        double elapsed = static_cast<double>(now_utc_ticks - state->last_end_observed_utc_ticks) / static_cast<double>(TICKS_PER_SECOND);
        double growth = static_cast<double>(end_time_ticks - state->last_end_ticks) / static_cast<double>(TICKS_PER_SECOND);
        if (growth > 0.0)
        {
            double tol = std::max(0.75, elapsed * 0.25);
            bool grows_with_clock = elapsed >= 0.5 && elapsed <= 15.0 && growth >= 0.5 &&
                                    std::abs(growth - elapsed) <= tol;
            state->growth_samples = grows_with_clock ? (state->growth_samples + 1) : 0;
            if (state->growth_samples >= 2)
            {
                state->is_live = 1;
            }
            else if (!grows_with_clock && seek_window_start_ticks <= 0)
            {
                state->is_live = 0;
            }
        }
        else if (is_playing != 0 && elapsed > 15.0 && seek_window_start_ticks <= 0)
        {
            state->is_live = 0;
            state->growth_samples = 0;
        }
    }

    if (end_time_ticks != state->last_end_ticks || state->has_last_end_observed == 0)
    {
        state->last_end_ticks = end_time_ticks;
        state->last_end_observed_utc_ticks = now_utc_ticks;
        state->has_last_end_observed = 1;
    }
    state->was_playing = is_playing != 0 ? 1u : 0u;
    *out_is_live = state->is_live;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_timeline_classifier_observe(VnTimelineClassifierState* state,
    uint8_t identity_changed,
    int64_t end_time_ticks,
    int64_t seek_window_start_ticks,
    int64_t now_utc_ticks,
    uint8_t is_playing,
    uint8_t* out_is_live) noexcept
{
    return vnotch::services::MediaTimelineClassifier::vn_timeline_classifier_observe_impl(state, identity_changed, end_time_ticks, seek_window_start_ticks, now_utc_ticks, is_playing, out_is_live);
}

