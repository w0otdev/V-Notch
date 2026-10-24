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

} // extern "C"
