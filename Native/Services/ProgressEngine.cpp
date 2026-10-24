#include "vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services
{

class ProgressEngine final
{
    static constexpr int64_t kTicksPerMillisecond = 10000LL;
    static constexpr int64_t kTicksPerSecond = 10000000LL;
    static constexpr int64_t kTicksPerMinute = 60LL * kTicksPerSecond;
    static constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;

    static double NormalizePlaybackRateRaw(double reported_rate) noexcept
    {
        if (!std::isfinite(reported_rate) || reported_rate <= 0.0)
        {
            return 1.0;
        }
        return std::clamp(reported_rate, 0.5, 2.5);
    }

public:
    static double NormalizeSnapshotPlaybackRate(double reported_rate, bool is_youtube) noexcept
    {
        double r = NormalizePlaybackRateRaw(reported_rate);
        if (is_youtube && std::abs(r - 1.0) <= 0.12)
        {
            r = 1.0;
        }
        return (std::abs(r - 1.0) <= 0.035) ? 1.0 : r;
    }

    static int64_t CalculateMaxCompensationWindowTicks(bool is_youtube, int64_t duration_ticks) noexcept
    {
        if (!is_youtube)
        {
            return 10LL * kTicksPerSecond;
        }
        if (duration_ticks > 0)
        {
            int64_t dur_win = duration_ticks + 5LL * kTicksPerSecond;
            int64_t four_hours = 4LL * kTicksPerHour;
            return (dur_win < four_hours) ? dur_win : four_hours;
        }
        return 2LL * kTicksPerMinute;
    }

    static int64_t ComputeEffectiveSnapshotPositionTicks(
        int64_t pos_ticks,
        int64_t dur_ticks,
        bool is_playing,
        bool is_youtube,
        bool is_indeterminate_effective,
        double playback_rate,
        int64_t snapshot_ts_ticks,
        int64_t now_utc_ticks) noexcept
    {
        if (is_indeterminate_effective || !is_playing || snapshot_ts_ticks <= 0 ||
            snapshot_ts_ticks > now_utc_ticks + 250LL * kTicksPerMillisecond)
        {
            return pos_ticks;
        }

        int64_t age_ticks = now_utc_ticks - snapshot_ts_ticks;
        if (age_ticks <= 100LL * kTicksPerMillisecond)
        {
            return pos_ticks;
        }

        int64_t max_win = CalculateMaxCompensationWindowTicks(is_youtube, dur_ticks);
        if (age_ticks > max_win)
        {
            return pos_ticks;
        }

        double norm_rate = NormalizePlaybackRateRaw(playback_rate);
        double age_sec = static_cast<double>(age_ticks) / static_cast<double>(kTicksPerSecond);
        int64_t delta_ticks = static_cast<int64_t>( std::nearbyint(age_sec * norm_rate * static_cast<double>(kTicksPerSecond)) );
        int64_t effective = pos_ticks + delta_ticks;

        if (dur_ticks > 0 && effective > dur_ticks)
        {
            effective = dur_ticks;
        }

        if (dur_ticks > 0 &&
            static_cast<double>(pos_ticks) < 5.0 * static_cast<double>(kTicksPerSecond) &&
            static_cast<double>(effective) > static_cast<double>(dur_ticks) * 0.9)
        {
            return pos_ticks;
        }

        return effective;
    }

    static int64_t PredictPositionTicks(
        int64_t base_pos_ticks,
        int64_t base_time_ticks,
        int64_t now_utc_ticks,
        double playback_rate,
        bool is_indeterminate,
        bool is_playing,
        int32_t state) noexcept
    {
        // ProgressState.Playing == 2, ProgressState.Seeking == 4
        if (is_indeterminate || !is_playing || (state != 2 && state != 4) || base_time_ticks <= 0)
        {
            return base_pos_ticks;
        }

        int64_t elapsed_ticks = now_utc_ticks - base_time_ticks;
        if (elapsed_ticks < 0)
        {
            elapsed_ticks = 0;
        }
        double elapsed_sec = static_cast<double>(elapsed_ticks) / static_cast<double>(kTicksPerSecond);
        int64_t add_ticks = static_cast<int64_t>(std::nearbyint(elapsed_sec * playback_rate * static_cast<double>(kTicksPerSecond)));
        return base_pos_ticks + add_ticks;
    }

    static int64_t ClampPositionTicks(int64_t pos_ticks, int64_t dur_ticks) noexcept
    {
        if (pos_ticks < 0)
        {
            return 0;
        }
        if (dur_ticks > 0 && pos_ticks > dur_ticks)
        {
            return dur_ticks;
        }
        return pos_ticks;
    }

    static bool DidDurationChange(int64_t prev_ticks, int64_t curr_ticks, bool is_youtube) noexcept
    {
        if (prev_ticks <= 0 || curr_ticks <= 0)
        {
            return (prev_ticks > 0) != (curr_ticks > 0);
        }
        double prev_sec = static_cast<double>(prev_ticks) / static_cast<double>(kTicksPerSecond);
        double curr_sec = static_cast<double>(curr_ticks) / static_cast<double>(kTicksPerSecond);
        double diff_sec = std::abs(curr_sec - prev_sec);
        double pct_threshold = is_youtube ? 0.05 : 0.02;
        double abs_threshold = is_youtube ? 3.0 : 1.0;
        double min_delta = std::max(abs_threshold, prev_sec * pct_threshold);
        return diff_sec >= min_delta;
    }

    static int64_t ComputeSmoothCorrectionTicks(
        int64_t diff_ticks,
        int64_t predicted_now_ticks,
        int64_t dur_ticks,
        bool is_youtube) noexcept
    {
        double diff_sec = static_cast<double>(diff_ticks) / static_cast<double>(kTicksPerSecond);
        double pred_sec = static_cast<double>(predicted_now_ticks) / static_cast<double>(kTicksPerSecond);

        if (!is_youtube && diff_ticks < 0)
        {
            double corrected_sec = pred_sec + diff_sec * 0.15;
            double min_allowed = pred_sec - 0.05;
            if (corrected_sec < min_allowed)
            {
                corrected_sec = min_allowed;
            }
            int64_t ticks = static_cast<int64_t>(std::nearbyint(corrected_sec * static_cast<double>(kTicksPerSecond)));
            return ClampPositionTicks(ticks, dur_ticks);
        }

        double corrected_sec2 = pred_sec + diff_sec * 0.9;
        if (diff_ticks < 0)
        {
            double min_allowed_back = pred_sec - 0.4;
            if (corrected_sec2 < min_allowed_back)
            {
                corrected_sec2 = min_allowed_back;
            }
        }
        int64_t ticks2 = static_cast<int64_t>(std::nearbyint(corrected_sec2 * static_cast<double>(kTicksPerSecond)));
        return ClampPositionTicks(ticks2, dur_ticks);
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_progress_engine_normalize_playback_rate(
    double reported_rate,
    uint8_t is_youtube,
    double* out_rate) noexcept
{
    if (out_rate == nullptr) return VN_ERR_INVALID_ARG;
    *out_rate = vnotch::services::ProgressEngine::NormalizeSnapshotPlaybackRate(reported_rate, is_youtube != 0u);
    return VN_OK;
}

VN_EXPORT int32_t vn_progress_engine_effective_snapshot_position(
    int64_t pos_ticks,
    int64_t dur_ticks,
    uint8_t is_playing,
    uint8_t is_youtube,
    uint8_t is_indeterminate_effective,
    double playback_rate,
    int64_t snapshot_ts_ticks,
    int64_t now_utc_ticks,
    int64_t* out_effective_ticks) noexcept
{
    if (out_effective_ticks == nullptr) return VN_ERR_INVALID_ARG;
    *out_effective_ticks = vnotch::services::ProgressEngine::ComputeEffectiveSnapshotPositionTicks(
        pos_ticks, dur_ticks, is_playing != 0u, is_youtube != 0u, is_indeterminate_effective != 0u,
        playback_rate, snapshot_ts_ticks, now_utc_ticks);
    return VN_OK;
}

VN_EXPORT int32_t vn_progress_engine_predict_position(
    int64_t base_pos_ticks,
    int64_t base_time_ticks,
    int64_t now_utc_ticks,
    double playback_rate,
    uint8_t is_indeterminate,
    uint8_t is_playing,
    int32_t state,
    int64_t* out_predicted_ticks) noexcept
{
    if (out_predicted_ticks == nullptr) return VN_ERR_INVALID_ARG;
    *out_predicted_ticks = vnotch::services::ProgressEngine::PredictPositionTicks(
        base_pos_ticks, base_time_ticks, now_utc_ticks, playback_rate, is_indeterminate != 0u, is_playing != 0u, state);
    return VN_OK;
}

VN_EXPORT int32_t vn_progress_engine_clamp_position(
    int64_t pos_ticks,
    int64_t dur_ticks,
    int64_t* out_clamped_ticks) noexcept
{
    if (out_clamped_ticks == nullptr) return VN_ERR_INVALID_ARG;
    *out_clamped_ticks = vnotch::services::ProgressEngine::ClampPositionTicks(pos_ticks, dur_ticks);
    return VN_OK;
}

VN_EXPORT int32_t vn_progress_engine_did_duration_change(
    int64_t prev_ticks,
    int64_t curr_ticks,
    uint8_t is_youtube,
    uint8_t* out_changed) noexcept
{
    if (out_changed == nullptr) return VN_ERR_INVALID_ARG;
    *out_changed = vnotch::services::ProgressEngine::DidDurationChange(prev_ticks, curr_ticks, is_youtube != 0u) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_progress_engine_smooth_correction(
    int64_t diff_ticks,
    int64_t predicted_now_ticks,
    int64_t dur_ticks,
    uint8_t is_youtube,
    int64_t* out_corrected_ticks) noexcept
{
    if (out_corrected_ticks == nullptr) return VN_ERR_INVALID_ARG;
    *out_corrected_ticks = vnotch::services::ProgressEngine::ComputeSmoothCorrectionTicks(
        diff_ticks, predicted_now_ticks, dur_ticks, is_youtube != 0u);
    return VN_OK;
}
