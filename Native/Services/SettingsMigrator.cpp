#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services {

class SettingsMigrator final {
public:
    static int32_t RoundToEven(double value) noexcept {
        const double fl = std::floor(value);
        const double diff = value - fl;
        if (diff > 0.5) return static_cast<int32_t>(fl + 1.0);
        if (diff < 0.5) return static_cast<int32_t>(fl);
        const int64_t fl_int = static_cast<int64_t>(fl);
        return (fl_int % 2 == 0) ? static_cast<int32_t>(fl_int) : static_cast<int32_t>(fl_int + 1);
    }

    static int32_t ComputeDynamicIslandWidthFromV3(int32_t width) noexcept {
        return RoundToEven(static_cast<double>(width) * 1.12 / 10.0) * 10;
    }

    static constexpr int32_t ClampAnimationFps(int32_t parsed_fps, int32_t min_fps, int32_t max_fps) noexcept {
        return std::clamp(parsed_fps, min_fps, max_fps);
    }

    static constexpr bool IsSupportedSettingsVersion(int32_t version, int32_t current_version) noexcept {
        return version >= 0 && version <= current_version;
    }

    static constexpr bool ShouldRevokeSpotifyCanvasConsent(
        bool has_accepted,
        bool enable_canvas,
        bool allow_online,
        int32_t consent_version) noexcept {
        return !has_accepted && (enable_canvas || allow_online || consent_version != 0);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_settings_migrator_compute_dynamic_island_width_v3(
    int32_t width,
    int32_t* out_width) noexcept {
    if (!out_width) return VN_E_INVALID_ARG;
    *out_width = vnotch::services::SettingsMigrator::ComputeDynamicIslandWidthFromV3(width);
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_migrator_clamp_animation_fps(
    int32_t parsed_fps,
    int32_t min_fps,
    int32_t max_fps,
    int32_t* out_fps) noexcept {
    if (!out_fps || min_fps > max_fps) return VN_E_INVALID_ARG;
    *out_fps = vnotch::services::SettingsMigrator::ClampAnimationFps(parsed_fps, min_fps, max_fps);
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_migrator_is_supported_version(
    int32_t version,
    int32_t current_version,
    uint8_t* out_supported) noexcept {
    if (!out_supported) return VN_E_INVALID_ARG;
    *out_supported = vnotch::services::SettingsMigrator::IsSupportedSettingsVersion(version, current_version) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_migrator_should_revoke_canvas_consent(
    uint8_t has_accepted,
    uint8_t enable_canvas,
    uint8_t allow_online,
    int32_t consent_version,
    uint8_t* out_revoke) noexcept {
    if (!out_revoke) return VN_E_INVALID_ARG;
    *out_revoke = vnotch::services::SettingsMigrator::ShouldRevokeSpotifyCanvasConsent(
        has_accepted != 0, enable_canvas != 0, allow_online != 0, consent_version) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
