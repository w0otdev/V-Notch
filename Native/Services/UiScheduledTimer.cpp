#include "../vnotch_common.h"
#include "../vnotch_native.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>

namespace vnotch::services {

class UiScheduledTimer final {
public:
    static int64_t ComputeDueTimestamp(
        int64_t now_timestamp,
        double interval_seconds,
        int64_t stopwatch_frequency) noexcept {
        if (stopwatch_frequency <= 0) {
            return now_timestamp;
        }
        const double safe_seconds = (std::isfinite(interval_seconds) && interval_seconds > 0.0)
            ? interval_seconds
            : 0.0;
        const double clamped_ms = std::max(1.0, safe_seconds * 1000.0);
        const double raw_ticks = (clamped_ms / 1000.0) * static_cast<double>(stopwatch_frequency);
        constexpr double kMaxSafeInt64Double = 9223372036854774784.0; // Largest IEEE-754 double < INT64_MAX
        const int64_t max_headroom = now_timestamp > 0
            ? (std::numeric_limits<int64_t>::max() - now_timestamp)
            : std::numeric_limits<int64_t>::max();
        if (!std::isfinite(raw_ticks) || raw_ticks >= kMaxSafeInt64Double) {
            return now_timestamp + max_headroom;
        }
        const int64_t delta_ticks = std::min<int64_t>(
            static_cast<int64_t>(std::max(0.0, raw_ticks)),
            max_headroom);
        return now_timestamp + delta_ticks;
    }

    static double ComputeWakeUpDelayMs(
        int64_t next_timestamp,
        int64_t now_timestamp,
        int64_t stopwatch_frequency) noexcept {
        if (stopwatch_frequency <= 0 || next_timestamp <= now_timestamp) return 1.0;
        const uint64_t delta_ticks = static_cast<uint64_t>(next_timestamp) - static_cast<uint64_t>(now_timestamp);
        const double milliseconds = (static_cast<double>(delta_ticks) * 1000.0) /
                                    static_cast<double>(stopwatch_frequency);
        if (!std::isfinite(milliseconds)) return static_cast<double>(INT32_MAX);
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
        const double safe_max = (std::isfinite(max_scroll) && max_scroll > 0.0) ? max_scroll : 0.0;
        const double clamped_target = std::isfinite(target_offset)
            ? std::clamp(target_offset, 0.0, safe_max)
            : 0.0;
        if (!std::isfinite(current_offset) || !std::isfinite(target_offset) || !std::isfinite(velocity)) {
            out_next_offset = clamped_target;
            out_next_velocity = 0.0;
            out_settled = 1;
            return;
        }

        const double safe_dt = (std::isfinite(dt_seconds) && dt_seconds > 0.0) ? dt_seconds : (1.0 / 60.0);
        const double frames = std::clamp(safe_dt * 60.0, 0.25, 3.0);
        const double safe_friction = std::isfinite(friction) ? std::clamp(friction, 0.0, 0.999) : 0.0;
        const double safe_min_vel = (std::isfinite(min_velocity) && min_velocity > 0.0) ? min_velocity : 0.05;

        const double diff = clamped_target - current_offset;
        const double next_vel = velocity * std::pow(safe_friction, frames);
        const double alpha = 1.0 - std::pow(1.0 - 0.18, frames);
        const double step = diff * alpha + next_vel * (0.4 * frames);
        if (!std::isfinite(step) || !std::isfinite(next_vel)) {
            out_next_offset = clamped_target;
            out_next_velocity = 0.0;
            out_settled = 1;
            return;
        }

        const double next_off = std::clamp(current_offset + step, 0.0, safe_max);
        if (std::abs(clamped_target - next_off) < safe_min_vel && std::abs(next_vel) < safe_min_vel) {
            out_next_offset = clamped_target;
            out_next_velocity = 0.0;
            out_settled = 1;
            return;
        }
        out_next_offset = next_off;
        out_next_velocity = next_vel;
        out_settled = 0;
    }

    static int32_t ComputeProgressiveBlurCascade(
        int32_t item_count,
        int32_t base_delay_ms,
        int32_t direction,
        uint8_t is_menu_list,
        VnProgressiveBlurStep* out_steps,
        int32_t out_cap,
        int32_t* out_written) noexcept {
        if (!out_steps || !out_written || out_cap < 0 || item_count < 0) {
            return VN_E_INVALID_ARG;
        }
        const int32_t count = std::min(item_count, out_cap);
        *out_written = count;
        if (count == 0) {
            return VN_OK;
        }

        const int32_t dir_sign = direction < 0 ? -1 : 1;
        const int32_t clamped_base_delay = std::max(0, base_delay_ms);
        const int32_t denom = std::max(1, count - 1);
        const double step_stagger_ms = is_menu_list != 0 ? 38.0 : 50.0;
        const double min_blur = is_menu_list != 0 ? 12.0 : 14.0;
        const double max_blur = is_menu_list != 0 ? 26.0 : 24.0;
        const double base_slide = is_menu_list != 0 ? 10.0 : 14.0;

        for (int32_t i = 0; i < count; ++i) {
            const double t = count > 1 ? static_cast<double>(i) / static_cast<double>(denom) : 0.0;
            const double damped_index = static_cast<double>(i) * (1.0 - 0.14 * t);
            const double raw_stagger = std::clamp(
                damped_index * step_stagger_ms,
                0.0,
                static_cast<double>(std::numeric_limits<int32_t>::max()));
            const int64_t stagger_ms = std::llround(raw_stagger);

            VnProgressiveBlurStep& step = out_steps[i];
            step.delay_ms = static_cast<int32_t>(std::min<int64_t>(
                static_cast<int64_t>(clamped_base_delay) + stagger_ms,
                std::numeric_limits<int32_t>::max()));
            step.fade_duration_ms = static_cast<int32_t>(std::lround(380.0 + t * 160.0));
            step.blur_duration_ms = static_cast<int32_t>(std::lround(500.0 + t * 220.0));
            step.slide_duration_ms = static_cast<int32_t>(std::lround(500.0 + t * 160.0));
            step.start_blur_radius = min_blur + (max_blur - min_blur) * t;
            step.start_opacity = 0.0;
            step.start_translate_y = static_cast<double>(dir_sign) * (base_slide + t * 6.0);
        }

        return VN_OK;
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

VN_EXPORT int32_t __stdcall vn_ui_compute_progressive_blur_cascade(
    int32_t item_count,
    int32_t base_delay_ms,
    int32_t direction,
    uint8_t is_menu_list,
    VnProgressiveBlurStep* out_steps,
    int32_t out_cap,
    int32_t* out_written) noexcept {
    return vnotch::services::UiScheduledTimer::ComputeProgressiveBlurCascade(
        item_count,
        base_delay_ms,
        direction,
        is_menu_list,
        out_steps,
        out_cap,
        out_written);
}

} // extern "C"
