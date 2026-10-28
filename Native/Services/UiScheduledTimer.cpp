#include "../vnotch_common.h"
#include "../vnotch_native.h"
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
            const int32_t stagger_ms = static_cast<int32_t>(std::lround(damped_index * step_stagger_ms));

            VnProgressiveBlurStep& step = out_steps[i];
            step.delay_ms = clamped_base_delay + stagger_ms;
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
