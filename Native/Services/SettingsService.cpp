#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <string_view>

namespace vnotch::services {

#pragma pack(push, 1)
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
    uint64_t extended_magic;
    double animation_speed;
    int32_t hover_expand_delay;
    int32_t hover_collapse_delay;
    int32_t hover_zone_margin;
    int32_t idle_auto_hide_delay;
    double media_blur_brightness_boost;
    double media_blur_dark_overlay;
    double compact_expand_multiplier;
    double medium_expand_multiplier;
    double large_expand_multiplier;
    int32_t spotlight_ai_words_per_second;
    int32_t gpu_preference;
    VnLiquidGlassScalars liquid_glass;
    uint8_t has_liquid_glass_custom;
    VnLiquidGlassScalars liquid_glass_custom;
};
#pragma pack(pop)

class SettingsService final {
public:
    static constexpr uint64_t kExtendedNormalizationMagic = 0x564E5F4E4F524D32ULL;

    static void ClampIntField(int32_t& field, int32_t min_val, int32_t max_val, bool& changed) noexcept {
        const int32_t clamped = std::clamp(field, min_val, max_val);
        if (clamped != field) {
            field = clamped;
            changed = true;
        }
    }

    static void ClampDoubleField(double& field, double min_val, double max_val, double fallback, bool& changed) noexcept {
        if (!std::isfinite(field)) {
            field = fallback;
            changed = true;
            return;
        }
        const double clamped = std::clamp(field, min_val, max_val);
        if (std::abs(clamped - field) > std::numeric_limits<double>::denorm_min()) {
            field = clamped;
            changed = true;
        }
    }

    static void NormalizeLiquidGlassScalars(VnLiquidGlassScalars& g, bool& changed) noexcept {
        ClampDoubleField(g.blur_amount, 0.0, 1.0, 0.3, changed);
        ClampDoubleField(g.refraction, 0.0, 1.5, 1.0, changed);
        ClampDoubleField(g.edge_bend, 0.0, 10.0, 1.65, changed);
        ClampDoubleField(g.chromatic_aberration, 0.0, 1.0, 0.56, changed);
        ClampDoubleField(g.edge_highlight, 0.0, 1.0, 0.1, changed);
        ClampDoubleField(g.touch_light, 0.0, 3.0, 0.9, changed);
        ClampDoubleField(g.specular, 0.0, 1.0, 0.0, changed);
        ClampDoubleField(g.fresnel, 0.0, 1.0, 0.0, changed);
        ClampDoubleField(g.distortion, 0.0, 1.0, 0.32, changed);
        ClampIntField(g.corner_radius, 0, 50, changed);
        ClampDoubleField(g.z_radius, 0.05, 0.60, 0.23, changed);
        ClampDoubleField(g.opacity, 0.0, 1.0, 1.0, changed);
        ClampDoubleField(g.saturation, -1.0, 1.0, 0.15, changed);
        ClampDoubleField(g.brightness, -0.5, 0.5, -0.05, changed);
        ClampDoubleField(g.shadow_opacity, 0.0, 1.0, 0.85, changed);
        ClampIntField(g.shadow_spread, 0, 40, changed);
        ClampIntField(g.bevel_mode, 0, 1, changed);
        ClampIntField(g.target_fps, 0, 240, changed);
        ClampIntField(g.variant, 0, 1, changed);
        ClampDoubleField(g.power_factor, 0.5, 10.0, 3.0, changed);
        ClampDoubleField(g.refraction_a, 0.0, 20.0, 0.7, changed);
        ClampDoubleField(g.refraction_b, 0.0, 20.0, 2.3, changed);
        ClampDoubleField(g.refraction_c, 0.0, 20.0, 5.2, changed);
        ClampDoubleField(g.refraction_d, 0.0, 20.0, 6.9, changed);
        ClampDoubleField(g.f_power, 0.1, 10.0, 1.0, changed);
        ClampDoubleField(g.noise, 0.0, 1.0, 0.066, changed);
        ClampDoubleField(g.glow_weight, -2.0, 2.0, 0.692, changed);
        ClampDoubleField(g.glow_bias, -2.0, 2.0, -0.040, changed);
        ClampDoubleField(g.glow_edge0, -2.0, 2.0, 0.441, changed);
        ClampDoubleField(g.glow_edge1, -2.0, 2.0, -0.474, changed);
    }

    static int32_t RoundToEven(double value) noexcept {
        if (!std::isfinite(value)) return 0;
        const double fl = std::floor(value);
        const double diff = value - fl;
        if (diff > 0.5) return static_cast<int32_t>(fl + 1.0);
        if (diff < 0.5) return static_cast<int32_t>(fl);
        const int64_t fl_int = static_cast<int64_t>(fl);
        return (fl_int % 2 == 0) ? static_cast<int32_t>(fl_int) : static_cast<int32_t>(fl_int + 1);
    }

    static bool NormalizeNumericSettings(VnSettingsNormalizationParams& p) noexcept {
        bool changed = false;

        ClampIntField(p.width, 100, 4000, changed);
        ClampIntField(p.height, 20, 200, changed);
        ClampIntField(p.corner_radius, 0, 50, changed);

        if (p.dynamic_island_width < 100 || p.dynamic_island_width > 4000) {
            p.dynamic_island_width = std::clamp(RoundToEven(static_cast<double>(p.width) * 1.12 / 10.0) * 10, 100, 4000);
            changed = true;
        }

        if (p.dynamic_island_height < 24 || p.dynamic_island_height > 300) {
            p.dynamic_island_height = 40;
            changed = true;
        }

        ClampIntField(p.notification_duration, 1000, 30000, changed);

        int32_t min_fps = p.min_fps > 0 ? p.min_fps : 30;
        int32_t max_fps = p.max_fps > 0 ? p.max_fps : 240;
        if (min_fps > max_fps) {
            std::swap(min_fps, max_fps);
            p.min_fps = min_fps;
            p.max_fps = max_fps;
            changed = true;
        }
        ClampIntField(p.animation_fps, min_fps, max_fps, changed);

        ClampDoubleField(p.spotify_canvas_brightness, 0.2, 1.0, 0.6, changed);
        ClampDoubleField(p.opacity, 0.1, 1.0, 0.95, changed);

        if (p.extended_magic != kExtendedNormalizationMagic) {
            return changed;
        }

        ClampDoubleField(p.animation_speed, 0.5, 2.0, 2.0, changed);
        ClampIntField(p.hover_expand_delay, 0, 500, changed);
        ClampIntField(p.hover_collapse_delay, 100, 2000, changed);
        ClampIntField(p.hover_zone_margin, 10, 100, changed);
        ClampIntField(p.idle_auto_hide_delay, 1, 60000, changed);
        ClampDoubleField(p.media_blur_brightness_boost, 0.0, 2.0, 2.0, changed);
        ClampDoubleField(p.media_blur_dark_overlay, 0.0, 1.0, 0.0, changed);
        ClampDoubleField(p.compact_expand_multiplier, 1.0, 1.6, 1.2, changed);
        ClampDoubleField(p.medium_expand_multiplier, 1.4, 2.6, 1.8, changed);
        ClampDoubleField(p.large_expand_multiplier, 2.0, 4.0, 2.5, changed);
        ClampIntField(p.spotlight_ai_words_per_second, 2, 30, changed);
        ClampIntField(p.gpu_preference, 0, 2, changed);

        NormalizeLiquidGlassScalars(p.liquid_glass, changed);
        if (p.has_liquid_glass_custom != 0) {
            NormalizeLiquidGlassScalars(p.liquid_glass_custom, changed);
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

    static int32_t CleanManualCity(
        std::u16string_view city,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t* out_len) noexcept {
        char16_t temp[100]{};
        int32_t count = 0;
        for (char16_t c : city) {
            if (!IsControlChar(c)) {
                temp[count++] = c;
                if (count == 100) break;
            }
        }

        const std::u16string_view trimmed = vnotch::vn_trim(std::u16string_view(temp, static_cast<size_t>(count)));
        return vnotch::copy_out_u16(trimmed, out_buf, out_cap, out_len);
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
        if (std::isfinite(a) && std::isfinite(b)) {
            return std::abs(a - b) <= 0.0001;
        }
        if (std::isnan(a) && std::isnan(b)) {
            return allow_nan_equal;
        }
        if (std::isinf(a) && std::isinf(b)) {
            return (a > 0.0) == (b > 0.0);
        }
        return false;
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
    if (out_cap < 0 || !out_len || city_len < 0 || (!city && city_len > 0) || (!out_buf && out_cap > 0)) return VN_E_INVALID_ARG;
    std::u16string_view sv(city ? city : u"", static_cast<size_t>(city_len));
    return vnotch::services::SettingsService::CleanManualCity(sv, out_buf, out_cap, out_len);
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
    if (!out_value || !std::isfinite(min_val) || !std::isfinite(max_val) || min_val > max_val) return VN_E_INVALID_ARG;
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
