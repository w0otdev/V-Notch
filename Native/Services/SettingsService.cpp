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

} // extern "C"
