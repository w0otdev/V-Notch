#include "../vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class Debouncer final {
public:
    static constexpr bool ShouldExecuteThrottleImmediately(
        int64_t now_utc_ticks,
        int64_t last_execution_utc_ticks,
        int64_t interval_ticks) noexcept {
        return (now_utc_ticks - last_execution_utc_ticks) >= interval_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_debouncer_should_execute_throttle_immediately(
    int64_t now_utc_ticks,
    int64_t last_execution_utc_ticks,
    int64_t interval_ticks,
    uint8_t* out_immediate) noexcept {
    if (!out_immediate) return VN_E_INVALID_ARG;
    *out_immediate = vnotch::services::Debouncer::ShouldExecuteThrottleImmediately(
        now_utc_ticks, last_execution_utc_ticks, interval_ticks) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
