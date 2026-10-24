#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class TimelinePositionSolver final
    {
    public:
        static constexpr bool has_finite_duration_ticks(int64_t duration_ticks) noexcept
        {
            return duration_ticks > 0 && duration_ticks <= (30LL * TICKS_PER_DAY);
        }
        
        static constexpr bool is_indeterminate_ticks(int64_t duration_ticks, bool reported_indeterminate) noexcept
        {
            return reported_indeterminate || !has_finite_duration_ticks(duration_ticks);
        }
        
        static constexpr int64_t duration_compensation_window_ticks(int64_t duration_ticks) noexcept
        {
            int64_t window = duration_ticks > 0
                ? (duration_ticks + 5LL * TICKS_PER_SECOND)
                : (10LL * TICKS_PER_MINUTE);
            int64_t four_hours = 4LL * TICKS_PER_HOUR;
            return window < four_hours ? window : four_hours;
        }

        static int32_t vn_timeline_solver_solve_impl(const VnTimelineSolveInputs* x, VnTimelineSolveResult* out_res) noexcept
        {
    if (x == nullptr || out_res == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    int64_t duration = x->end_time_ticks - x->start_time_ticks;
    if (duration <= 0)
    {
        duration = x->max_seek_time_ticks;
    }
    bool indeterminate = is_indeterminate_ticks(duration, x->is_live_timeline != 0);
    if (!has_finite_duration_ticks(duration))
    {
        duration = 0;
    }

    double duration_sec = static_cast<double>(duration) / static_cast<double>(TICKS_PER_SECOND);
    double rem_ms = static_cast<double>(duration - x->position_ticks) / static_cast<double>(TICKS_PER_MILLISECOND);
    bool is_nearly_end = duration_sec > 0.0 && rem_ms < 800.0;
    bool is_browser_live_edge = (x->is_browser_timeline_track != 0) && duration > 0 &&
                                (duration - x->position_ticks) <= (5LL * TICKS_PER_SECOND);

    int64_t chosen_position = x->position_ticks;
    bool force_start_position = false;
    if (!indeterminate && x->is_browser_timeline_track == 0 && x->is_new_track != 0 && is_nearly_end)
    {
        chosen_position = 0;
        force_start_position = true;
    }

    if (!indeterminate && !is_browser_live_edge && x->is_browser_timeline_track != 0 &&
        x->is_new_track != 0 && x->is_playing != 0)
    {
        int64_t timeline_age = x->now_utc_ticks - x->last_updated_utc_ticks;
        if (timeline_age < 0)
        {
            timeline_age = 0;
        }
        double chosen_sec = static_cast<double>(chosen_position) / static_cast<double>(TICKS_PER_SECOND);
        bool suspicious_carry_over = chosen_sec > 20.0 &&
                                     (duration_sec <= 0.0 || chosen_sec > duration_sec * 0.2);
        bool stale_at_start = timeline_age > (900LL * TICKS_PER_MILLISECOND);
        if (suspicious_carry_over && stale_at_start)
        {
            chosen_position = 0;
            force_start_position = true;
        }
    }

    int64_t raw_updated_utc = x->last_updated_utc_ticks;
    int64_t timeline_updated_utc = force_start_position ? x->now_utc_ticks : raw_updated_utc;
    int64_t timeline_updated_offset = force_start_position ? x->now_offset_ticks : x->last_updated_offset_ticks;

    if (!indeterminate && !force_start_position && x->is_playing != 0)
    {
        int64_t now_utc = x->now_utc_ticks;
        int64_t latency = now_utc - raw_updated_utc;
        int64_t max_window = 15LL * TICKS_PER_SECOND;
        if (x->is_browser_timeline_track != 0)
        {
            max_window = (x->is_initial_or_big_change != 0)
                ? duration_compensation_window_ticks(duration)
                : (2LL * TICKS_PER_MINUTE);
        }
        else if (x->is_initial_or_big_change != 0)
        {
            max_window = duration_compensation_window_ticks(duration);
        }

        bool valid_timestamp = raw_updated_utc > 0 &&
                               raw_updated_utc <= (now_utc + 250LL * TICKS_PER_MILLISECOND) &&
                               latency >= 0 &&
                               latency <= max_window;

        double latency_ms = static_cast<double>(latency) / static_cast<double>(TICKS_PER_MILLISECOND);
        if (valid_timestamp && latency_ms > 100.0)
        {
            double rate = x->playback_rate > 0.0 ? x->playback_rate : 1.0;
            double latency_sec = static_cast<double>(latency) / static_cast<double>(TICKS_PER_SECOND);
            double comp_ticks_d = latency_sec * rate * static_cast<double>(TICKS_PER_SECOND);
            int64_t delta_ticks = static_cast<int64_t>(comp_ticks_d + (comp_ticks_d >= 0.0 ? 0.5 : -0.5));
            int64_t compensated = chosen_position + delta_ticks;
            if (duration > 0 && compensated > duration)
            {
                compensated = duration;
            }
            if (compensated > chosen_position)
            {
                chosen_position = compensated;
                timeline_updated_utc = now_utc;
                timeline_updated_offset = x->now_offset_ticks;
            }
        }
    }

    if (chosen_position < 0)
    {
        chosen_position = 0;
    }
    if (duration > 0 && chosen_position > duration)
    {
        chosen_position = duration;
    }

    out_res->position_ticks = chosen_position;
    out_res->duration_ticks = duration;
    out_res->last_updated_utc_ticks = timeline_updated_utc;
    out_res->last_updated_offset_ticks = timeline_updated_offset;
    out_res->is_indeterminate = indeterminate ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_timeline_solver_solve(const VnTimelineSolveInputs* x, VnTimelineSolveResult* out_res) noexcept
{
    return vnotch::services::TimelinePositionSolver::vn_timeline_solver_solve_impl(x, out_res);
}

