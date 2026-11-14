#include "../vnotch_native.h"

#pragma comment(lib, "advapi32.lib")

namespace
{
    [[nodiscard]] inline uint8_t round_to_byte(double value) noexcept
    {
        if (!(value > 0.0)) return 0;
        if (value >= 255.0) return 255;
        const int32_t rounded = static_cast<int32_t>(std::rint(value));
        return static_cast<uint8_t>(std::clamp(rounded, 0, 255));
    }

    inline void build_fresnel_tint_core(
        uint8_t red,
        uint8_t green,
        uint8_t blue,
        uint8_t& out_r,
        uint8_t& out_g,
        uint8_t& out_b) noexcept
    {
        const double luminance = 0.299 * red + 0.587 * green + 0.114 * blue;
        double r = luminance + (red - luminance) * 1.30;
        double g = luminance + (green - luminance) * 1.30;
        double b = luminance + (blue - luminance) * 1.30;
        const double peak = std::max(r, std::max(g, b));

        if (peak < 12.0)
        {
            out_r = 104;
            out_g = 116;
            out_b = 130;
            return;
        }

        if (peak < 96.0)
        {
            const double lift = 96.0 / peak;
            r *= lift;
            g *= lift;
            b *= lift;
        }

        out_r = round_to_byte(r);
        out_g = round_to_byte(g);
        out_b = round_to_byte(b);
    }

    inline void interpolate_color_core(
        uint8_t from_r,
        uint8_t from_g,
        uint8_t from_b,
        uint8_t to_r,
        uint8_t to_g,
        uint8_t to_b,
        double amount,
        uint8_t& out_r,
        uint8_t& out_g,
        uint8_t& out_b) noexcept
    {
        const double t = std::clamp(amount, 0.0, 1.0);
        out_r = round_to_byte(static_cast<double>(from_r) + (static_cast<int32_t>(to_r) - static_cast<int32_t>(from_r)) * t);
        out_g = round_to_byte(static_cast<double>(from_g) + (static_cast<int32_t>(to_g) - static_cast<int32_t>(from_g)) * t);
        out_b = round_to_byte(static_cast<double>(from_b) + (static_cast<int32_t>(to_b) - static_cast<int32_t>(from_b)) * t);
    }
}

VN_EXPORT int32_t __stdcall vn_glass_binder_compute_cpu_rim_opacities(
    double edge_highlight,
    double specular,
    double fresnel,
    double chroma,
    VnCpuRimOpacities* out_opacities) noexcept
{
    if (!out_opacities) return VN_ERR_INVALID_ARG;

    const double edge = std::clamp(edge_highlight, 0.0, 1.0);
    const double spec = std::clamp(specular, 0.0, 1.0);
    const double fres = std::clamp(fresnel, 0.0, 1.0);
    const double spectral = std::clamp(chroma, 0.0, 2.0);
    const double fresnel_energy = std::sqrt(fres);
    const double spectral_opacity = std::clamp(spectral * 0.30 + edge * 0.10, 0.0, 0.52);

    out_opacities->active_fresnel = fres;
    out_opacities->rim = std::sqrt(edge) * 0.82;
    out_opacities->depth_rim = std::clamp(edge * 0.34 + fres * 0.24, 0.0, 0.46);
    out_opacities->fresnel_bloom = fresnel_energy * 0.30;
    out_opacities->fresnel = fresnel_energy * 0.94;
    out_opacities->inner_fresnel = fresnel_energy * 0.64;
    out_opacities->specular = spec * 0.92;
    out_opacities->cool_rim = spectral_opacity;
    out_opacities->warm_rim = spectral_opacity * 0.76;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_binder_build_fresnel_tint(
    uint8_t red,
    uint8_t green,
    uint8_t blue,
    uint8_t* out_r,
    uint8_t* out_g,
    uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    build_fresnel_tint_core(red, green, blue, *out_r, *out_g, *out_b);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_binder_interpolate_color(
    uint8_t from_r,
    uint8_t from_g,
    uint8_t from_b,
    uint8_t to_r,
    uint8_t to_g,
    uint8_t to_b,
    double amount,
    uint8_t* out_r,
    uint8_t* out_g,
    uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    interpolate_color_core(from_r, from_g, from_b, to_r, to_g, to_b, amount, *out_r, *out_g, *out_b);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_binder_step_dynamic_fresnel(
    int64_t now_ticks,
    int64_t last_ticks,
    double active_fresnel_level,
    double light_x,
    double light_y,
    double contrast,
    uint8_t optic_r,
    uint8_t optic_g,
    uint8_t optic_b,
    double cur_x,
    double cur_y,
    double cur_contrast,
    uint8_t cur_tint_r,
    uint8_t cur_tint_g,
    uint8_t cur_tint_b,
    VnDynamicFresnelStep* out_step) noexcept
{
    if (!out_step) return VN_ERR_INVALID_ARG;

    const double elapsed_seconds = (last_ticks == 0)
        ? 1.0
        : std::clamp(static_cast<double>(now_ticks - last_ticks) / 1000.0, 0.0, 0.25);
    const double response = 1.0 - std::exp(-elapsed_seconds * 1.1);

    const double target_x = std::clamp(0.42 + light_x * 0.08, 0.34, 0.50);
    const double target_y = std::clamp(0.34 + light_y * 0.07, 0.27, 0.41);
    const double next_x = cur_x + (target_x - cur_x) * response;
    const double next_y = cur_y + (target_y - cur_y) * response;
    const double next_contrast = cur_contrast + (std::clamp(contrast, 0.0, 1.0) - cur_contrast) * response;

    uint8_t target_r = 0, target_g = 0, target_b = 0;
    build_fresnel_tint_core(optic_r, optic_g, optic_b, target_r, target_g, target_b);

    uint8_t tint_r = 0, tint_g = 0, tint_b = 0;
    interpolate_color_core(cur_tint_r, cur_tint_g, cur_tint_b, target_r, target_g, target_b, response, tint_r, tint_g, tint_b);

    uint8_t bright_r = 0, bright_g = 0, bright_b = 0;
    interpolate_color_core(tint_r, tint_g, tint_b, 255, 255, 255, 0.68, bright_r, bright_g, bright_b);

    uint8_t mid_r = 0, mid_g = 0, mid_b = 0;
    interpolate_color_core(tint_r, tint_g, tint_b, 255, 255, 255, 0.42, mid_r, mid_g, mid_b);

    const double fresnel_energy = std::sqrt(std::clamp(active_fresnel_level, 0.0, 1.0));
    const double contrast_response = 0.94 + next_contrast * 0.12;

    out_step->fresnel_x = next_x;
    out_step->fresnel_y = next_y;
    out_step->fresnel_contrast = next_contrast;
    out_step->tint_r = tint_r;
    out_step->tint_g = tint_g;
    out_step->tint_b = tint_b;
    out_step->bright_r = bright_r;
    out_step->bright_g = bright_g;
    out_step->bright_b = bright_b;
    out_step->mid_r = mid_r;
    out_step->mid_g = mid_g;
    out_step->mid_b = mid_b;
    out_step->inner_shadow_a = round_to_byte(82.0 + next_contrast * 48.0);
    out_step->origin_x = std::clamp(0.5 + (next_x - 0.5) * 0.72, 0.30, 0.62);
    out_step->origin_y = std::clamp(0.5 + (next_y - 0.5) * 0.72, 0.26, 0.58);
    out_step->radius_x = 0.84 - next_contrast * 0.08;
    out_step->radius_y = 0.90 - next_contrast * 0.06;
    out_step->bloom_opacity = std::clamp(fresnel_energy * (0.24 + next_contrast * 0.08), 0.0, 0.42);
    out_step->fresnel_opacity = std::clamp(fresnel_energy * 0.98 * contrast_response, 0.0, 1.0);
    out_step->inner_fresnel_opacity = std::clamp(fresnel_energy * (0.58 + next_contrast * 0.10), 0.0, 0.76);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_binder_read_system_transparency(uint8_t* out_enabled) noexcept
{
    if (!out_enabled) return VN_ERR_INVALID_ARG;

    DWORD val = 1;
    DWORD cb = sizeof(val);
    const LSTATUS status = ::RegGetValueW(
        HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
        L"EnableTransparency",
        RRF_RT_REG_DWORD,
        nullptr,
        &val,
        &cb);
    if (status == ERROR_SUCCESS && cb == sizeof(DWORD))
    {
        *out_enabled = (val == 1) ? 1u : 0u;
    }
    else
    {
        *out_enabled = 1u;
    }
    return VN_OK;
}
