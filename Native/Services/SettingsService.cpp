#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <string_view>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnSettingsNormalizationParams {
    int32_t width;
    int32_t height;
    int32_t corner_radius;
    int32_t dynamic_island_width;
    int32_t dynamic_island_height;
    int32_t notification_duration;
    int32_t animation_fps;
    int32_t min_fps;
    int32_t max_fps;
    double spotify_canvas_brightness;
    double opacity;
};

struct VnLiquidGlassScalars {
    double blur_amount;
    double refraction;
    double edge_bend;
    double chromatic_aberration;
    double edge_highlight;
    double touch_light;
    double specular;
    double fresnel;
    double distortion;
    int32_t corner_radius;
    double z_radius;
    double opacity;
    double saturation;
    double brightness;
    double shadow_opacity;
    int32_t shadow_spread;
    int32_t bevel_mode;
    int32_t target_fps;
    int32_t variant;
    double power_factor;
    double refraction_a;
    double refraction_b;
    double refraction_c;
    double refraction_d;
    double f_power;
    double noise;
    double glow_weight;
    double glow_bias;
    double glow_edge0;
    double glow_edge1;
    uint8_t hide_from_screen_capture;
    uint8_t use_gpu_refraction;
    uint8_t use_ultimate_gpu;
};
#pragma pack(pop)

class SettingsService final {
public:
    static int32_t RoundToEven(double value) noexcept {
        const double fl = std::floor(value);
        const double diff = value - fl;
        if (diff > 0.5) return static_cast<int32_t>(fl + 1.0);
        if (diff < 0.5) return static_cast<int32_t>(fl);
        const int64_t fl_int = static_cast<int64_t>(fl);
        return (fl_int % 2 == 0) ? static_cast<int32_t>(fl_int) : static_cast<int32_t>(fl_int + 1);
    }

    static bool NormalizeNumericSettings(VnSettingsNormalizationParams& p) noexcept {
        bool changed = false;

        const int32_t clamped_width = std::clamp(p.width, 100, 4000);
        if (clamped_width != p.width) { p.width = clamped_width; changed = true; }

        const int32_t clamped_height = std::clamp(p.height, 20, 200);
        if (clamped_height != p.height) { p.height = clamped_height; changed = true; }

        const int32_t clamped_radius = std::clamp(p.corner_radius, 0, 50);
        if (clamped_radius != p.corner_radius) { p.corner_radius = clamped_radius; changed = true; }

        if (p.dynamic_island_width < 100 || p.dynamic_island_width > 4000) {
            p.dynamic_island_width = std::clamp(RoundToEven(static_cast<double>(p.width) * 1.12 / 10.0) * 10, 100, 4000);
            changed = true;
        }

        if (p.dynamic_island_height < 24 || p.dynamic_island_height > 300) {
            p.dynamic_island_height = 40;
            changed = true;
        }

        const int32_t clamped_notif = std::clamp(p.notification_duration, 1000, 30000);
        if (clamped_notif != p.notification_duration) { p.notification_duration = clamped_notif; changed = true; }

        const int32_t clamped_fps = std::clamp(p.animation_fps, p.min_fps, p.max_fps);
        if (clamped_fps != p.animation_fps) { p.animation_fps = clamped_fps; changed = true; }

        const double canvas_brightness = std::clamp(p.spotify_canvas_brightness, 0.2, 1.0);
        if (std::abs(canvas_brightness - p.spotify_canvas_brightness) > std::numeric_limits<double>::denorm_min()) {
            p.spotify_canvas_brightness = canvas_brightness;
            changed = true;
        }

        const double clamped_opacity = std::clamp(p.opacity, 0.1, 1.0);
        if (std::abs(clamped_opacity - p.opacity) > std::numeric_limits<double>::denorm_min()) {
            p.opacity = clamped_opacity;
            changed = true;
        }

        return changed;
    }

    static constexpr bool IsLanguageCodeValid(std::u16string_view lang) noexcept {
        if (lang.empty() || lang.size() > 10) return false;
        bool has_non_ws = false;
        for (char16_t c : lang) {
            if (c == u'\0' || c == u'\r' || c == u'\n' || c == u'/' || c == u'\\') {
                return false;
            }
            if (!vnotch::vn_is_whitespace(c)) {
                has_non_ws = true;
            }
        }
        return has_non_ws;
    }

    static constexpr bool IsControlChar(char16_t c) noexcept {
        return c <= 0x001Fu || (c >= 0x007Fu && c <= 0x009Fu);
    }

    static int32_t CleanManualCity(std::u16string_view city, char16_t* out_buf, int32_t out_cap) noexcept {
        char16_t temp[100]{};
        int32_t count = 0;
        for (char16_t c : city) {
            if (!IsControlChar(c)) {
                temp[count++] = c;
                if (count == 100) break;
            }
        }

        std::u16string_view trimmed = vnotch::vn_trim(std::u16string_view(temp, static_cast<size_t>(count)));
        const int32_t copy_len = std::min(static_cast<int32_t>(trimmed.size()), out_cap);
        for (int32_t i = 0; i < copy_len; ++i) {
            out_buf[i] = trimmed[static_cast<size_t>(i)];
        }
        return copy_len;
    }

    static constexpr bool CheckRequiresRestart(
        int32_t imported_gpu,
        int32_t current_gpu,
        std::u16string_view imported_priority,
        std::u16string_view current_priority) noexcept {
        if (imported_gpu != current_gpu) return true;
        if (!vnotch::vn_equals_icase_ascii(imported_priority, current_priority)) return true;
        return false;
    }

    static bool DoubleEquals(double a, double b, bool allow_nan_equal) noexcept {
        if (!(std::abs(a - b) > 0.0001)) return true;
        return allow_nan_equal && std::isnan(a) && std::isnan(b);
    }

    static bool LiquidGlassEquals(const VnLiquidGlassScalars& a, const VnLiquidGlassScalars& b) noexcept {
        return DoubleEquals(a.blur_amount, b.blur_amount, true)
            && DoubleEquals(a.refraction, b.refraction, true)
            && DoubleEquals(a.edge_bend, b.edge_bend, true)
            && DoubleEquals(a.chromatic_aberration, b.chromatic_aberration, true)
            && DoubleEquals(a.edge_highlight, b.edge_highlight, true)
            && DoubleEquals(a.touch_light, b.touch_light, true)
            && DoubleEquals(a.specular, b.specular, true)
            && DoubleEquals(a.fresnel, b.fresnel, true)
            && DoubleEquals(a.distortion, b.distortion, true)
            && a.corner_radius == b.corner_radius
            && DoubleEquals(a.z_radius, b.z_radius, true)
            && DoubleEquals(a.opacity, b.opacity, true)
            && DoubleEquals(a.saturation, b.saturation, true)
            && DoubleEquals(a.brightness, b.brightness, true)
            && DoubleEquals(a.shadow_opacity, b.shadow_opacity, true)
            && a.shadow_spread == b.shadow_spread
            && a.bevel_mode == b.bevel_mode
            && a.target_fps == b.target_fps
            && a.variant == b.variant
            && DoubleEquals(a.power_factor, b.power_factor, true)
            && DoubleEquals(a.refraction_a, b.refraction_a, true)
            && DoubleEquals(a.refraction_b, b.refraction_b, true)
            && DoubleEquals(a.refraction_c, b.refraction_c, true)
            && DoubleEquals(a.refraction_d, b.refraction_d, true)
            && DoubleEquals(a.f_power, b.f_power, true)
            && DoubleEquals(a.noise, b.noise, true)
            && DoubleEquals(a.glow_weight, b.glow_weight, true)
            && DoubleEquals(a.glow_bias, b.glow_bias, true)
            && DoubleEquals(a.glow_edge0, b.glow_edge0, true)
            && DoubleEquals(a.glow_edge1, b.glow_edge1, true)
            && a.hide_from_screen_capture == b.hide_from_screen_capture
            && a.use_gpu_refraction == b.use_gpu_refraction
            && a.use_ultimate_gpu == b.use_ultimate_gpu;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_settings_service_normalize_numeric(
    vnotch::services::VnSettingsNormalizationParams* inout_params,
    uint8_t* out_changed) noexcept {
    if (!inout_params || !out_changed) return VN_E_INVALID_ARG;
    *out_changed = vnotch::services::SettingsService::NormalizeNumericSettings(*inout_params) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_service_is_language_valid(
    const char16_t* lang,
    int32_t lang_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid || lang_len < 0 || (!lang && lang_len > 0)) return VN_E_INVALID_ARG;
    std::u16string_view sv(lang ? lang : u"", static_cast<size_t>(lang_len));
    *out_valid = vnotch::services::SettingsService::IsLanguageCodeValid(sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_service_clean_manual_city(
    const char16_t* city,
    int32_t city_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap < 0 || !out_len || city_len < 0 || (!city && city_len > 0)) return VN_E_INVALID_ARG;
    std::u16string_view sv(city ? city : u"", static_cast<size_t>(city_len));
    *out_len = vnotch::services::SettingsService::CleanManualCity(sv, out_buf, out_cap);
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_service_check_requires_restart(
    int32_t imported_gpu,
    int32_t current_gpu,
    const char16_t* imported_priority,
    int32_t imported_priority_len,
    const char16_t* current_priority,
    int32_t current_priority_len,
    uint8_t* out_requires_restart) noexcept {
    if (!out_requires_restart || imported_priority_len < 0 || current_priority_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view imp_sv(imported_priority ? imported_priority : u"", static_cast<size_t>(imported_priority_len));
    std::u16string_view cur_sv(current_priority ? current_priority : u"", static_cast<size_t>(current_priority_len));
    *out_requires_restart = vnotch::services::SettingsService::CheckRequiresRestart(
        imported_gpu, current_gpu, imp_sv, cur_sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_settings_clamp_int(
    int32_t value,
    int32_t min_val,
    int32_t max_val,
    int32_t* out_value) noexcept {
    if (!out_value || min_val > max_val) return VN_E_INVALID_ARG;
    *out_value = std::clamp(value, min_val, max_val);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_settings_clamp_double(
    double value,
    double min_val,
    double max_val,
    double fallback_if_non_finite,
    double* out_value) noexcept {
    if (!out_value || min_val > max_val) return VN_E_INVALID_ARG;
    *out_value = std::isfinite(value) ? std::clamp(value, min_val, max_val) : fallback_if_non_finite;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_settings_double_equals(
    double a,
    double b,
    uint8_t allow_nan_equal,
    uint8_t* out_equal) noexcept {
    if (!out_equal) return VN_E_INVALID_ARG;
    *out_equal = vnotch::services::SettingsService::DoubleEquals(a, b, allow_nan_equal != 0) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_liquid_glass_config_equals(
    const vnotch::services::VnLiquidGlassScalars* a,
    const vnotch::services::VnLiquidGlassScalars* b,
    uint8_t* out_equal) noexcept {
    if (!a || !b || !out_equal) return VN_E_INVALID_ARG;
    *out_equal = vnotch::services::SettingsService::LiquidGlassEquals(*a, *b) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
