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
VN_EXPORT int32_t __stdcall vn_glass_analyze_backdrop_samples(
    const int32_t* rgb_samples,
    int32_t sample_len,
    int32_t columns,
    int32_t rows,
    VnGlassBackdropOpticsOut* out_optics) noexcept
{
    if (!out_optics)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (!rgb_samples || columns <= 0 || rows <= 0 || columns > 4096 || rows > 4096)
    {
        *out_optics = VnGlassBackdropOpticsOut{128, 128, 128, 0, 0.0, 0.0, 0.0, 0};
        return VN_OK;
    }

    const int64_t count64 = static_cast<int64_t>(columns) * static_cast<int64_t>(rows);
    if (count64 <= 0 || count64 > 4096 || sample_len < count64)
    {
        *out_optics = VnGlassBackdropOpticsOut{128, 128, 128, 0, 0.0, 0.0, 0.0, 0};
        return VN_OK;
    }
    const int32_t count = static_cast<int32_t>(count64);

    double stack_lum[256];
    std::vector<double> heap_lum;
    double* luminance = stack_lum;
    if (count > 256)
    {
        try
        {
            heap_lum.resize(static_cast<size_t>(count));
            luminance = heap_lum.data();
        }
        catch (...)
        {
            *out_optics = VnGlassBackdropOpticsOut{128, 128, 128, 0, 0.0, 0.0, 0.0, 0};
            return VN_ERR_FAIL;
        }
    }

    double sum_r = 0.0, sum_g = 0.0, sum_b = 0.0, sum_l = 0.0;
    double min_l = 255.0, max_l = 0.0;
    for (int32_t i = 0; i < count; ++i)
    {
        const int32_t rgb = rgb_samples[i];
        const int32_t r = (rgb >> 16) & 0xFF;
        const int32_t g = (rgb >> 8) & 0xFF;
        const int32_t b = rgb & 0xFF;
        const double l = 0.299 * r + 0.587 * g + 0.114 * b;
        luminance[i] = l;
        sum_r += r;
        sum_g += g;
        sum_b += b;
        sum_l += l;
        min_l = std::min(min_l, l);
        max_l = std::max(max_l, l);
    }

    const double mean_l = sum_l / count;
    double weighted_x = 0.0, weighted_y = 0.0, total_weight = 0.0;
    for (int32_t y = 0; y < rows; ++y)
    {
        const double ny = rows == 1 ? 0.0 : (y * 2.0 / (rows - 1) - 1.0);
        for (int32_t x = 0; x < columns; ++x)
        {
            const double highlight = std::max(luminance[y * columns + x] - mean_l, 0.0);
            const double weight = highlight * highlight;
            if (weight <= 1e-6) continue;
            const double nx = columns == 1 ? 0.0 : (x * 2.0 / (columns - 1) - 1.0);
            weighted_x += nx * weight;
            weighted_y += ny * weight;
            total_weight += weight;
        }
    }

    const double contrast = std::clamp((max_l - min_l) / 255.0, 0.0, 1.0);
    const double directional_strength = std::clamp(contrast * 2.2, 0.0, 1.0);
    const double light_x = total_weight > 1e-6 ? weighted_x / total_weight * directional_strength : 0.0;
    const double light_y = total_weight > 1e-6 ? weighted_y / total_weight * directional_strength : 0.0;

    out_optics->red = static_cast<uint8_t>(std::clamp(static_cast<int32_t>(std::nearbyint(sum_r / count)), 0, 255));
    out_optics->green = static_cast<uint8_t>(std::clamp(static_cast<int32_t>(std::nearbyint(sum_g / count)), 0, 255));
    out_optics->blue = static_cast<uint8_t>(std::clamp(static_cast<int32_t>(std::nearbyint(sum_b / count)), 0, 255));
    out_optics->reserved = 0;
    out_optics->light_x = std::clamp(light_x, -1.0, 1.0);
    out_optics->light_y = std::clamp(light_y, -1.0, 1.0);
    out_optics->contrast = contrast;
    out_optics->outside_rgb = 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_sample_backdrop_optics(
    const uint8_t* dib_bits,
    int32_t src_w,
    int32_t src_h,
    int32_t sample_x,
    int32_t sample_y,
    int32_t sample_w,
    int32_t sample_h,
    VnGlassBackdropOpticsOut* out_optics) noexcept
{
    if (!dib_bits || !out_optics || src_w <= 0 || src_h <= 0 || sample_w <= 0 || sample_h <= 0 ||
        static_cast<int64_t>(src_w) * static_cast<int64_t>(src_h) > (INT32_MAX / 4))
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int32_t kColumns = 8;
    constexpr int32_t kRows = 8;
    int32_t samples[kColumns * kRows];
    const int64_t stride = static_cast<int64_t>(src_w) * 4LL;
    const int64_t max_src_x = static_cast<int64_t>(src_w - 1);
    const int64_t max_src_y = static_cast<int64_t>(src_h - 1);

    for (int32_t gy = 0; gy < kRows; ++gy)
    {
        const int64_t raw_y = static_cast<int64_t>(sample_y) +
                              (static_cast<int64_t>(sample_h) - 1LL) * static_cast<int64_t>(gy) / (kRows - 1);
        const int64_t y = std::clamp<int64_t>(raw_y, 0LL, max_src_y);
        const uint8_t* row = dib_bits + y * stride;
        for (int32_t gx = 0; gx < kColumns; ++gx)
        {
            const int64_t raw_x = static_cast<int64_t>(sample_x) +
                                  (static_cast<int64_t>(sample_w) - 1LL) * static_cast<int64_t>(gx) / (kColumns - 1);
            const int64_t x = std::clamp<int64_t>(raw_x, 0LL, max_src_x);
            const uint8_t* p = row + x * 4LL;
            samples[gy * kColumns + gx] = (static_cast<int32_t>(p[2]) << 16) |
                                          (static_cast<int32_t>(p[1]) << 8) |
                                          static_cast<int32_t>(p[0]);
        }
    }

    vn_glass_analyze_backdrop_samples(samples, kColumns * kRows, kColumns, kRows, out_optics);

    const int64_t outside_x = std::clamp<int64_t>(
        static_cast<int64_t>(sample_x) + static_cast<int64_t>(sample_w) / 2LL, 0LL, max_src_x);
    const int64_t below_y = static_cast<int64_t>(sample_y) + static_cast<int64_t>(sample_h) + 16LL;
    const int64_t outside_y = (below_y >= 0LL && below_y < static_cast<int64_t>(src_h))
        ? below_y
        : std::clamp<int64_t>(static_cast<int64_t>(sample_y) - 16LL, 0LL, max_src_y);
    const uint8_t* outside = dib_bits + outside_y * stride + outside_x * 4LL;
    out_optics->outside_rgb = (static_cast<int32_t>(outside[2]) << 16) |
                              (static_cast<int32_t>(outside[1]) << 8) |
                              static_cast<int32_t>(outside[0]);
    return VN_OK;
}


}
