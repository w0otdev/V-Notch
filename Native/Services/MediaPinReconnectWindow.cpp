#include "../vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class MediaPinReconnectWindow final {
public:
    static constexpr bool HasGracePeriodExpired(
        bool waiting,
        int64_t elapsed_ticks,
        int64_t grace_period_ticks) noexcept {
        return waiting && elapsed_ticks >= grace_period_ticks;
    }

    static constexpr int64_t ComputeRemainingGraceTicks(
        int64_t elapsed_ticks,
        int64_t grace_period_ticks) noexcept {
        return grace_period_ticks - elapsed_ticks;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_pin_reconnect_has_expired(
    uint8_t waiting,
    int64_t elapsed_ticks,
    int64_t grace_period_ticks,
    uint8_t* out_expired) noexcept {
    if (!out_expired) return VN_E_INVALID_ARG;
    *out_expired = vnotch::services::MediaPinReconnectWindow::HasGracePeriodExpired(
        waiting != 0, elapsed_ticks, grace_period_ticks) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_pin_reconnect_remaining_ticks(
    int64_t elapsed_ticks,
    int64_t grace_period_ticks,
    int64_t* out_remaining_ticks) noexcept {
    if (!out_remaining_ticks) return VN_E_INVALID_ARG;
    *out_remaining_ticks = vnotch::services::MediaPinReconnectWindow::ComputeRemainingGraceTicks(
        elapsed_ticks, grace_period_ticks);
    return VN_OK;
}

} // extern "C"
