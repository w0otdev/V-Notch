#include "../vnotch_common.h"
#include <cmath>
#include <cstdint>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnMediaPublishParams {
    uint8_t force_refresh;
    uint8_t metadata_changed;
    uint8_t thumbnail_changed;
    uint8_t playback_changed;
    uint8_t timeline_changed;
    uint8_t source_changed;
    uint8_t seek_capability_changed;
    uint8_t pip_changed;
    uint8_t throttle_changed;
    uint8_t is_throttled;
    uint8_t is_playing;
    int64_t position_ticks;
    int64_t last_position_ticks;
    int64_t duration_ticks;
    int64_t now_utc_ticks;
    int64_t startup_sync_until_utc_ticks;
};
#pragma pack(pop)

class MediaDetectionService final {
public:
    static constexpr double kTicksPerSecond = 10000000.0;

    static constexpr int32_t ResolveHeartbeatIntervalMs(int32_t detection_mode) noexcept {
        switch (detection_mode) {
            case 0: return 5000; // Idle
            case 1: return 3000; // AwaitingMetadata
            case 2: return 3000; // EventDriven
            case 3: return 1500; // ThrottledMedia
            default: return 3000;
        }
    }

    static constexpr bool ShouldSkipHeartbeat(
        int32_t detection_mode,
        int64_t last_event_ticks,
        int64_t now_utc_ticks) noexcept {
        if (detection_mode == 1) return true; // AwaitingMetadata
        if (detection_mode == 0 && last_event_ticks > 0) {
            const double elapsed_sec = static_cast<double>(now_utc_ticks - last_event_ticks) / kTicksPerSecond;
            if (elapsed_sec > 10.0) return true;
        }
        return false;
    }

    static bool EvaluateShouldPublish(const VnMediaPublishParams& p) noexcept {
        const double pos_diff_sec = std::abs(static_cast<double>(p.position_ticks - p.last_position_ticks) / kTicksPerSecond);
        const bool is_throttled = p.is_throttled != 0;
        const bool significant_jump = pos_diff_sec >= (is_throttled ? 5.0 : 1.5);
        const bool in_startup_sync = p.now_utc_ticks <= p.startup_sync_until_utc_ticks;
        const bool startup_timeline_sync = in_startup_sync &&
                                           (p.is_playing != 0) &&
                                           (p.duration_ticks > 0 || p.position_ticks > 0) &&
                                           pos_diff_sec >= 0.2;

        return (p.force_refresh != 0) ||
               (p.metadata_changed != 0) ||
               (p.thumbnail_changed != 0) ||
               (p.playback_changed != 0) ||
               (p.timeline_changed != 0) ||
               (p.source_changed != 0) ||
               (p.pip_changed != 0) ||
               (significant_jump && !is_throttled) ||
               (p.seek_capability_changed != 0) ||
               (p.throttle_changed != 0) ||
               startup_timeline_sync;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_detection_resolve_heartbeat_interval_ms(
    int32_t detection_mode,
    int32_t* out_interval_ms) noexcept {
    if (!out_interval_ms) return VN_E_INVALID_ARG;
    *out_interval_ms = vnotch::services::MediaDetectionService::ResolveHeartbeatIntervalMs(detection_mode);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_detection_should_skip_heartbeat(
    int32_t detection_mode,
    int64_t last_event_ticks,
    int64_t now_utc_ticks,
    uint8_t* out_skip) noexcept {
    if (!out_skip) return VN_E_INVALID_ARG;
    *out_skip = vnotch::services::MediaDetectionService::ShouldSkipHeartbeat(
        detection_mode, last_event_ticks, now_utc_ticks) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_detection_evaluate_should_publish(
    const vnotch::services::VnMediaPublishParams* params,
    uint8_t* out_should_publish) noexcept {
    if (!params || !out_should_publish) return VN_E_INVALID_ARG;
    *out_should_publish = vnotch::services::MediaDetectionService::EvaluateShouldPublish(*params) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
