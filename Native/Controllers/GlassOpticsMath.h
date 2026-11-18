#pragma once
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

namespace vnotch::controllers {
constexpr uint64_t Avalanche64(uint64_t x) noexcept
{
    x ^= x >> 33;
    x *= 0xff51afd7ed558ccdULL;
    x ^= x >> 33;
    x *= 0xc4ceb9fe1a85ec53ULL;
    x ^= x >> 33;
    return x;
}

inline double Smoother01(double x) noexcept
{
    const double t = std::clamp(x, 0.0, 1.0);
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

struct GlassEdge
{
    double inside, inward_x, inward_y;
};

inline GlassEdge RoundedRectEdge(
    double px, double py, double bx, double by, double top_radius, double bottom_radius) noexcept
{
    const double r = py < 0.0 ? top_radius : bottom_radius;
    const double qx = std::abs(px) - (bx - r);
    const double qy = (py < 0.0 ? -py : py) - (by - r);
    const double nx = px < 0.0 ? 1.0 : -1.0;
    const double ny = py < 0.0 ? 1.0 : -1.0;
    if (qx > 0.0 && qy > 0.0)
    {
        const double length = std::sqrt(qx * qx + qy * qy);
        return {r - length, nx * qx / length, ny * qy / length};
    }
    return qx > qy ? GlassEdge{r - qx, nx, 0.0} : GlassEdge{r - qy, 0.0, ny};
}

inline double GlassHash(int32_t ix, int32_t iy) noexcept
{
    uint32_t h = static_cast<uint32_t>(ix * 374761393 + iy * 668265263);
    h = (h ^ (h >> 13)) * 1274126177u;
    return static_cast<double>((h ^ (h >> 16)) & 0xFFFFFFu) * (1.0 / 16777216.0);
}

inline double ValueNoise(double px, double py) noexcept
{
    if (!std::isfinite(px) || !std::isfinite(py))
    {
        return 0.5;
    }
    const double clamped_px = std::clamp(px, -1e8, 1e8);
    const double clamped_py = std::clamp(py, -1e8, 1e8);
    const double floor_x = std::floor(clamped_px);
    const double floor_y = std::floor(clamped_py);
    const int32_t ix = static_cast<int32_t>(floor_x);
    const int32_t iy = static_cast<int32_t>(floor_y);
    const double fx = Smoother01(clamped_px - floor_x);
    const double fy = Smoother01(clamped_py - floor_y);
    const double h00 = GlassHash(ix, iy);
    const double h10 = GlassHash(ix + 1, iy);
    const double h01 = GlassHash(ix, iy + 1);
    const double h11 = GlassHash(ix + 1, iy + 1);
    const double top = h00 + (h10 - h00) * fx;
    const double bottom = h01 + (h11 - h01) * fx;
    return top + (bottom - top) * fy;
}

inline void PackSampleCoord(
    double sx,
    double sy,
    int32_t src_w,
    int32_t max_x,
    int32_t max_y,
    int32_t* out_idx,
    int32_t* out_aux) noexcept
{
    const double safe_sx = std::isfinite(sx)
        ? std::clamp(sx, -1.0, static_cast<double>(std::max(0, max_x)) + 1.0)
        : 0.0;
    const double safe_sy = std::isfinite(sy)
        ? std::clamp(sy, -1.0, static_cast<double>(std::max(0, max_y)) + 1.0)
        : 0.0;
    int32_t ix = static_cast<int32_t>(std::floor(safe_sx));
    int32_t iy = static_cast<int32_t>(std::floor(safe_sy));
    const double fx = safe_sx - ix;
    const double fy = safe_sy - iy;

    int32_t wx;
    if (ix < 0)
    {
        ix = 0;
        wx = 0;
    }
    else if (ix >= max_x)
    {
        if (max_x > 0)
        {
            ix = max_x - 1;
            wx = 256;
        }
        else
        {
            ix = 0;
            wx = 0;
        }
    }
    else
    {
        wx = std::clamp(static_cast<int32_t>(std::nearbyint(fx * 256.0)), 0, 256);
    }

    int32_t wy;
    if (iy < 0)
    {
        iy = 0;
        wy = 0;
    }
    else if (iy >= max_y)
    {
        if (max_y > 0)
        {
            iy = max_y - 1;
            wy = 256;
        }
        else
        {
            iy = 0;
            wy = 0;
        }
    }
    else
    {
        wy = std::clamp(static_cast<int32_t>(std::nearbyint(fy * 256.0)), 0, 256);
    }

    const int32_t flags = (max_x > 0 ? 1 : 0) | (max_y > 0 ? 2 : 0);
    *out_idx = (iy * src_w + ix) << 2;
    *out_aux = flags | (wx << 2) | (wy << 12);
}

inline int32_t BilerpChannelFast(
    const uint8_t* src,
    int32_t idx,
    int32_t aux,
    int32_t stride,
    int32_t chan) noexcept
{
    const int32_t wx = (aux >> 2) & 0x1FF;
    const int32_t wy = (aux >> 12) & 0x1FF;
    const uint8_t* p0 = src + idx + chan;
    const uint8_t* p1 = p0 + stride;
    const int32_t p00 = p0[0];
    const int32_t p10 = p0[4];
    const int32_t p01 = p1[0];
    const int32_t p11 = p1[4];
    const int32_t top = (p00 << 8) + (p10 - p00) * wx;
    const int32_t bot = (p01 << 8) + (p11 - p01) * wx;
    return ((top << 8) + (bot - top) * wy) >> 16;
}

inline int32_t BilerpChannel(
    const uint8_t* src,
    int32_t idx,
    int32_t aux,
    int32_t stride,
    int32_t chan) noexcept
{
    if ((aux & 3) == 3)
    {
        return BilerpChannelFast(src, idx, aux, stride, chan);
    }
    const int32_t step_x = (aux & 1) != 0 ? 4 : 0;
    const int32_t step_y = (aux & 2) != 0 ? stride : 0;
    const int32_t wx = (aux >> 2) & 0x1FF;
    const int32_t wy = (aux >> 12) & 0x1FF;

    const int32_t bas = idx + chan;
    const int32_t p00 = src[bas];
    const int32_t p10 = src[bas + step_x];
    const int32_t p01 = src[bas + step_y];
    const int32_t p11 = src[bas + step_x + step_y];

    const int32_t top = p00 * (256 - wx) + p10 * wx;
    const int32_t bot = p01 * (256 - wx) + p11 * wx;
    return (top * (256 - wy) + bot * wy) >> 16;
}

inline uint32_t PackAdjustedBgra(int b, int g, int r, float saturation, float offset) noexcept
{
    const __m128 rgb = _mm_cvtepi32_ps(_mm_set_epi32(0, r, g, b));
    const __m128 color = _mm_add_ps(_mm_mul_ps(rgb, _mm_set1_ps(saturation)), _mm_set1_ps(offset));
    // Saturating packs handle negative values. Preserve the scalar path's
    // zero for a channel that overflowed even though the inputs were finite.
    const __m128 finite = _mm_cmple_ps(color, _mm_set1_ps(std::numeric_limits<float>::max()));
    const __m128 capped = _mm_and_ps(finite, _mm_min_ps(color, _mm_set1_ps(255.0f)));
    const __m128i rounded = _mm_cvttps_epi32(_mm_add_ps(capped, _mm_set1_ps(0.5f)));
    const __m128i words = _mm_packs_epi32(rounded, rounded);
    return static_cast<uint32_t>(_mm_cvtsi128_si32(_mm_packus_epi16(words, words))) | 0xff000000u;
}

}
