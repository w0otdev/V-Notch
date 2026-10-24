#include "../vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class ZOrderManager final {
public:
    static constexpr bool ShouldThrottleTopmostAssert(
        bool force,
        int64_t now_tick,
        int64_t last_tick,
        int64_t throttle_ms) noexcept {
        return !force && last_tick != INT64_MIN && (now_tick - last_tick) < throttle_ms;
    }

    static constexpr void ComputeBurstDelays(
        int64_t duration_ms,
        bool aggressive,
        int64_t& out_first_delay_ms,
        int64_t& out_remaining_ms) noexcept {
        out_first_delay_ms = (aggressive && duration_ms > 250) ? 250 : duration_ms;
        out_remaining_ms = duration_ms - out_first_delay_ms;
    }

    static constexpr bool ShouldProcessForegroundEvent(
        uint32_t event_type,
        int64_t hwnd,
        int64_t self_hwnd) noexcept {
        // EVENT_SYSTEM_FOREGROUND == 0x0003
        return event_type == 0x0003u && hwnd != 0 && hwnd != self_hwnd;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_zorder_manager_should_throttle_assert(
    uint8_t force,
    int64_t now_tick,
    int64_t last_tick,
    int64_t throttle_ms,
    uint8_t* out_throttle) noexcept {
    if (!out_throttle) return VN_E_INVALID_ARG;
    *out_throttle = vnotch::services::ZOrderManager::ShouldThrottleTopmostAssert(
        force != 0, now_tick, last_tick, throttle_ms) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_zorder_manager_compute_burst_delays(
    int64_t duration_ms,
    uint8_t aggressive,
    int64_t* out_first_delay_ms,
    int64_t* out_remaining_ms) noexcept {
    if (!out_first_delay_ms || !out_remaining_ms) return VN_E_INVALID_ARG;
    vnotch::services::ZOrderManager::ComputeBurstDelays(
        duration_ms, aggressive != 0, *out_first_delay_ms, *out_remaining_ms);
    return VN_OK;
}

VN_EXPORT int32_t vn_zorder_manager_should_process_foreground(
    uint32_t event_type,
    int64_t hwnd,
    int64_t self_hwnd,
    uint8_t* out_process) noexcept {
    if (!out_process) return VN_E_INVALID_ARG;
    *out_process = vnotch::services::ZOrderManager::ShouldProcessForegroundEvent(
        event_type, hwnd, self_hwnd) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
