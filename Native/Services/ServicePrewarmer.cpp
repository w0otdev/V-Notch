#include "../vnotch_common.h"

namespace vnotch::services {

class ServicePrewarmer final {
public:
    static constexpr int32_t InitialWarmupDelayMs = 1200;

    static bool ShouldWarmupSpotlight(bool has_settings, bool enable_spotlight) noexcept {
        return !has_settings || enable_spotlight;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_service_prewarmer_get_initial_delay_ms(int32_t* out_delay_ms) noexcept {
    if (!out_delay_ms) return VN_E_INVALID_ARG;
    *out_delay_ms = vnotch::services::ServicePrewarmer::InitialWarmupDelayMs;
    return VN_OK;
}

VN_EXPORT int32_t vn_service_prewarmer_should_warmup_spotlight(
    uint8_t has_settings,
    uint8_t enable_spotlight,
    uint8_t* out_should_warmup) noexcept {
    if (!out_should_warmup) return VN_E_INVALID_ARG;
    *out_should_warmup = vnotch::services::ServicePrewarmer::ShouldWarmupSpotlight(
        has_settings != 0, enable_spotlight != 0) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
