#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace vnotch::services {

class UiScheduledTimer final {
public:
    static int64_t ComputeDueTimestamp(
        int64_t now_timestamp,
        double interval_seconds,
        int64_t stopwatch_frequency) noexcept {
        const double clamped_ms = std::max(1.0, interval_seconds * 1000.0);
        const int64_t delta_ticks = static_cast<int64_t>((clamped_ms / 1000.0) * static_cast<double>(stopwatch_frequency));
        return now_timestamp + delta_ticks;
    }

    static double ComputeWakeUpDelayMs(
        int64_t next_timestamp,
        int64_t now_timestamp,
        int64_t stopwatch_frequency) noexcept {
        if (stopwatch_frequency <= 0) return 1.0;
        const double milliseconds = static_cast<double>(next_timestamp - now_timestamp) * 1000.0 /
                                    static_cast<double>(stopwatch_frequency);
        return std::clamp(std::ceil(milliseconds), 1.0, static_cast<double>(INT32_MAX));
    }

    static void ComputeSmoothScrollStep(
        double current_offset,
        double target_offset,
        double velocity,
        double max_scroll,
        double friction,
        double min_velocity,
        double dt_seconds,
        double& out_next_offset,
        double& out_next_velocity,
        uint8_t& out_settled) noexcept {
        const double safe_max = std::max(0.0, max_scroll);
        const double clamped_target = std::clamp(target_offset, 0.0, safe_max);
        const double frames = std::clamp(dt_seconds > 0.0 ? dt_seconds * 60.0 : 1.0, 0.25, 3.0);
        const double diff = clamped_target - current_offset;
        const double next_vel = velocity * std::pow(std::clamp(friction, 0.0, 0.999), frames);
        const double alpha = 1.0 - std::pow(1.0 - 0.18, frames);
        const double step = diff * alpha + next_vel * (0.4 * frames);
        const double next_off = std::clamp(current_offset + step, 0.0, safe_max);
        if (std::abs(clamped_target - next_off) < min_velocity && std::abs(next_vel) < min_velocity) {
            out_next_offset = clamped_target;
            out_next_velocity = 0.0;
            out_settled = 1;
            return;
        }
        out_next_offset = next_off;
        out_next_velocity = next_vel;
        out_settled = 0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_ui_scheduled_timer_compute_due_timestamp(
    int64_t now_timestamp,
    double interval_seconds,
    int64_t stopwatch_frequency,
    int64_t* out_due_timestamp) noexcept {
    if (!out_due_timestamp) return VN_E_INVALID_ARG;
    *out_due_timestamp = vnotch::services::UiScheduledTimer::ComputeDueTimestamp(
        now_timestamp, interval_seconds, stopwatch_frequency);
    return VN_OK;
}

VN_EXPORT int32_t vn_ui_scheduled_timer_compute_wakeup_delay_ms(
    int64_t next_timestamp,
    int64_t now_timestamp,
    int64_t stopwatch_frequency,
    double* out_delay_ms) noexcept {
    if (!out_delay_ms) return VN_E_INVALID_ARG;
    *out_delay_ms = vnotch::services::UiScheduledTimer::ComputeWakeUpDelayMs(
        next_timestamp, now_timestamp, stopwatch_frequency);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_ui_smooth_scroll_step(
    double current_offset,
    double target_offset,
    double velocity,
    double max_scroll,
    double friction,
    double min_velocity,
    double dt_seconds,
    double* out_next_offset,
    double* out_next_velocity,
    uint8_t* out_settled) noexcept {
    if (!out_next_offset || !out_next_velocity || !out_settled) return VN_E_INVALID_ARG;
    vnotch::services::UiScheduledTimer::ComputeSmoothScrollStep(
        current_offset,
        target_offset,
        velocity,
        max_scroll,
        friction,
        min_velocity,
        dt_seconds,
        *out_next_offset,
        *out_next_velocity,
        *out_settled);
    return VN_OK;
}

} // extern "C"
