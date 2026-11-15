#include "vnotch_common.h"
#include "vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>

#include "GlassOpticsMath.h"

extern "C" {
VN_EXPORT int32_t __stdcall vn_glass_gaussian_box_radii(
    double sigma,
    int32_t passes,
    int32_t* out_radii,
    int32_t radii_cap,
    int32_t* out_written) noexcept
{
    if (!out_radii || !out_written || radii_cap <= 0)
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t count = std::max(1, passes);
    if (radii_cap < count)
    {
        *out_written = 0;
        return VN_ERR_BUFFER_TOO_SMALL;
    }

    *out_written = count;
    if (!std::isfinite(sigma) || sigma <= 0.0)
    {
        for (int32_t i = 0; i < count; ++i)
        {
            out_radii[i] = 0;
        }
        return VN_OK;
    }

    const double safe_sigma = std::min(sigma, 10000.0);
    const double ideal_width = std::sqrt((12.0 * safe_sigma * safe_sigma / count) + 1.0);
    int32_t lower_width = static_cast<int32_t>(std::floor(ideal_width));
    if ((lower_width & 1) == 0)
    {
        --lower_width;
    }
    lower_width = std::max(1, lower_width);
    const int32_t upper_width = lower_width + 2;

    const double numerator = 12.0 * safe_sigma * safe_sigma
        - static_cast<double>(count) * lower_width * lower_width
        - 4.0 * count * lower_width
        - 3.0 * count;
    int32_t lower_count = static_cast<int32_t>(std::nearbyint(
        numerator / (-4.0 * lower_width - 4.0)));
    lower_count = std::clamp(lower_count, 0, count);

    for (int32_t i = 0; i < count; ++i)
    {
        const int32_t width = i < lower_count ? lower_width : upper_width;
        out_radii[i] = (width - 1) / 2;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_compute_capture_interval_ms(
    int32_t configured_fps,
    int32_t display_refresh_hz,
    uint8_t ultimate_gpu,
    double* out_interval_ms) noexcept
{
    if (!out_interval_ms)
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t clamped_fps = std::clamp(configured_fps, 1, 1000);
    int32_t effective_fps = clamped_fps;
    if (display_refresh_hz > 0 && ultimate_gpu == 0)
    {
        effective_fps = std::min(clamped_fps, display_refresh_hz);
    }
    effective_fps = std::max(1, effective_fps);
    *out_interval_ms = 1000.0 / static_cast<double>(effective_fps);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_should_submit_frame(
    int64_t now_ticks,
    int64_t last_submit_ticks,
    int64_t stopwatch_freq,
    double active_interval_ms,
    uint8_t force_present,
    uint8_t* out_should_submit) noexcept
{
    if (!out_should_submit)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (force_present != 0 || last_submit_ticks <= 0 || stopwatch_freq <= 0 ||
        !std::isfinite(active_interval_ms) || active_interval_ms <= 0.0)
    {
        *out_should_submit = 1u;
        return VN_OK;
    }

    if (now_ticks <= last_submit_ticks)
    {
        *out_should_submit = 0u;
        return VN_OK;
    }
    const int64_t elapsed_ticks = now_ticks - last_submit_ticks;

    const double elapsed_ms = static_cast<double>(elapsed_ticks) * 1000.0 / static_cast<double>(stopwatch_freq);
    // Allow a 6% timing tolerance for compositor VBlank alignment without letting frames spin unthrottled.
    *out_should_submit = (elapsed_ms + 0.25 >= active_interval_ms * 0.94) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_score_d3d9_adapter(
    const char16_t* description,
    int32_t desc_len,
    uint32_t vendor_id,
    int64_t* out_score) noexcept
{
    if (!out_score || desc_len < 0 || (!description && desc_len > 0))
    {
        return VN_ERR_INVALID_ARG;
    }

    const std::u16string_view sv(description ? description : u"", static_cast<size_t>(desc_len));
    if (vnotch::vn_contains_icase_ascii(sv, u"Microsoft Basic") ||
        vnotch::vn_contains_icase_ascii(sv, u"WARP"))
    {
        *out_score = -1000;
        return VN_OK;
    }

    int64_t score = 100;
    // NVIDIA (0x10DE) or discrete Radeon / Arc markers
    if (vendor_id == 0x10DEu ||
        vnotch::vn_contains_icase_ascii(sv, u"NVIDIA") ||
        vnotch::vn_contains_icase_ascii(sv, u"GeForce") ||
        vnotch::vn_contains_icase_ascii(sv, u"RTX") ||
        vnotch::vn_contains_icase_ascii(sv, u"GTX") ||
        vnotch::vn_contains_icase_ascii(sv, u"Quadro"))
    {
        score += 100000;
    }
    else if (vnotch::vn_contains_icase_ascii(sv, u"Radeon RX") ||
             vnotch::vn_contains_icase_ascii(sv, u"Radeon Pro") ||
             vnotch::vn_contains_icase_ascii(sv, u"Intel(R) Arc"))
    {
        score += 90000;
    }
    else if (vendor_id == 0x1002u || vendor_id == 0x1022u)
    {
        // AMD APU / integrated Radeon vs Intel UHD
        score += 1000;
    }

    *out_score = score;
    return VN_OK;
}

}
