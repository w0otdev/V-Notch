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

namespace {
// A minified ray uses the first two coordinate pairs as the endpoints of
// its footprint. The last pair carries this tag and the dispersion weight.
constexpr int32_t FilteredFootprint = -1;
void ReadFootprint(const uint8_t* src, const int32_t* m, int stride, int& b, int& g, int& r) noexcept
{
    const int chroma = m[5]; // 0..256
    const int weights[] = {128 + chroma / 2, 128, 128 - 58 * chroma / 256};
    int colors[3];
    for (int channel = 0; channel < 3; ++channel)
    {
        const int outer = vnotch::controllers::BilerpChannel(src, m[0], m[1], stride, channel);
        const int inner = vnotch::controllers::BilerpChannel(src, m[2], m[3], stride, channel);
        colors[channel] = (outer * (256 - weights[channel]) + inner * weights[channel] + 128) >> 8;
    }
    b = colors[0]; g = colors[1]; r = colors[2];
}
}

extern "C" {
VN_EXPORT int32_t __stdcall vn_glass_build_refraction_lut(
    double f_power,
    double refraction_a,
    double refraction_b,
    double refraction_c,
    double refraction_d,
    double* out_lut,
    int32_t lut_len) noexcept
{
    if (!out_lut || lut_len <= 1 ||
        !std::isfinite(f_power) || !std::isfinite(refraction_a) ||
        !std::isfinite(refraction_b) || !std::isfinite(refraction_c) ||
        !std::isfinite(refraction_d))
    {
        return VN_ERR_INVALID_ARG;
    }
    const double f_pow = std::max(f_power, 0.1);
    const double inv_lut_max = 1.0 / static_cast<double>(lut_len - 1);
    const double base_val = std::max(refraction_c * 2.718281828459045, 0.0001);
    for (int32_t i = 0; i < lut_len; ++i)
    {
        const double dist_norm = static_cast<double>(i) * inv_lut_max;
        const double exponent = std::clamp(-refraction_d * dist_norm - refraction_a, -700.0, 700.0);
        const double p_val = std::pow(base_val, exponent);
        const double f_val = std::isfinite(p_val) ? (1.0 - refraction_b * p_val) : 0.0001;
        const double out_val = std::pow(std::max(f_val, 0.0001), f_pow);
        out_lut[i] = std::isfinite(out_val) ? out_val : 1.0;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_build_refraction_rows(
    const VnGlassMapParams* params,
    const double* lut,
    int32_t lut_len,
    int32_t y_start,
    int32_t y_end,
    int32_t* out_map) noexcept
{
    if (!params || !lut || !out_map || lut_len <= 1 || params->out_w <= 0 || params->out_h <= 0 ||
        params->src_w <= 0 || params->src_h <= 0 ||
        static_cast<int64_t>(params->src_w) * static_cast<int64_t>(params->src_h) > (INT32_MAX / 4) ||
        static_cast<int64_t>(params->out_w) * static_cast<int64_t>(params->out_h) > (INT32_MAX / 6) ||
        !std::isfinite(params->out_scale) ||
        !std::isfinite(params->top_corner_radius) ||
        !std::isfinite(params->bottom_corner_radius) ||
        !std::isfinite(params->edge_bend) ||
        !std::isfinite(params->chromatic_aberration) ||
        !std::isfinite(params->distortion) ||
        !std::isfinite(params->f_power) ||
        !std::isfinite(params->refraction_a) ||
        !std::isfinite(params->refraction_b) ||
        !std::isfinite(params->refraction_c) ||
        !std::isfinite(params->refraction_d) ||
        !std::isfinite(params->refraction) ||
        !std::isfinite(params->z_radius))
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t y0 = std::clamp(y_start, 0, params->out_h);
    const int32_t y1 = std::clamp(y_end, y0, params->out_h);
    const int32_t out_w = params->out_w;
    const int32_t src_w = params->src_w;
    const int32_t max_x = params->src_w - 1;
    const int32_t max_y = params->src_h - 1;

    const double half_x = static_cast<double>(params->notch_w) * 0.5;
    const double half_y = static_cast<double>(params->notch_h) * 0.5;
    const double cx = static_cast<double>(params->notch_off_x) + (static_cast<double>(params->notch_w) - 1.0) * 0.5;
    const double cy = static_cast<double>(params->notch_off_y) + (static_cast<double>(params->notch_h) - 1.0) * 0.5;
    const double min_half = std::max(0.0, std::min(half_x, half_y));

    const double top_r = std::clamp(params->top_corner_radius * params->out_scale, 0.0, min_half);
    const double bottom_r = std::clamp(params->bottom_corner_radius * params->out_scale, 0.0, min_half);
    const double u_chroma = std::clamp(params->chromatic_aberration, 0.0, 2.0);
    const double distort = std::clamp(params->distortion, 0.0, 2.0);
    const double refract_strength = std::clamp(params->refraction, 0.0, 3.0);
    const double rim = std::min(min_half, std::max(3.0,
        std::clamp(params->z_radius, 0.02, 0.95) * 100.0 * params->out_scale));
    const double inv_rim = 1.0 / std::max(rim, 1.0);
    // A notch attached to the screen has no exposed upper glass surface.
    const double lift = top_r < 0.5 ? rim * 2.0 + 1.0 : 0.0;
    const double bevel_shelf = params->bevel_mode >= 1 ? 0.22 : 0.0;
    const double bend = std::max(params->edge_bend, 0.0) *
        (refract_strength > 1e-4 ? (0.20 + 1.15 * refract_strength) : 0.0);
    const double dome_pull = (0.18 + bevel_shelf) * std::min(refract_strength, 1.8);
    const double lever = rim * 1.5 * bend;
    const double inv_half_x = 1.0 / std::max(half_x, 0.5);
    const double inv_half_y = 1.0 / std::max(half_y, 0.5);
    const double lut_scale = static_cast<double>(lut_len - 1);

    for (int32_t y = y0; y < y1; ++y)
    {
        const double ly = static_cast<double>(y) - cy;
        const double row_base_y = static_cast<double>(y) + static_cast<double>(params->margin) - static_cast<double>(params->capture_shift_y);
        int32_t* dst_row = out_map + static_cast<size_t>(y) * static_cast<size_t>(out_w) * 6;

        for (int32_t x = 0; x < out_w; ++x)
        {
            const double lx = static_cast<double>(x) - cx;
            double base_x = static_cast<double>(x) + static_cast<double>(params->margin) - static_cast<double>(params->capture_shift_x);
            double base_y = row_base_y;
            int32_t* cell = dst_row + static_cast<size_t>(x) * 6;

            const auto edge = vnotch::controllers::RoundedRectEdge(lx, ly + lift, half_x, half_y + lift, top_r, bottom_r);
            if (ly < -half_y || edge.inside <= 0.0 || edge.inside >= rim)
            {
                // These coordinates are integer capture pixels. No neighbours
                // are needed, including at the last source row or column.
                const int ix = static_cast<int>(std::clamp(base_x, 0.0, static_cast<double>(max_x)));
                const int iy = static_cast<int>(std::clamp(base_y, 0.0, static_cast<double>(max_y)));
                cell[0] = cell[2] = cell[4] = (iy * src_w + ix) * 4;
                cell[1] = cell[3] = cell[5] = 0;
                continue;
            }

            const double one_minus_dist = std::max(1.0 - edge.inside * inv_rim, 0.0);
            const double grazing = one_minus_dist * one_minus_dist;

            const double depth = edge.inside * inv_rim;
            const double pos = depth * depth * lut_scale;
            const int idx = std::min(static_cast<int>(pos), lut_len - 2);
            const double a = std::isfinite(lut[idx]) ? lut[idx] : 1.0;
            const double b = std::isfinite(lut[idx + 1]) ? lut[idx + 1] : a;
            const double ray = std::lerp(a, b, pos - idx) - grazing * dome_pull;
            const double factor = std::clamp(ray, 0.05, 1.25);
            const double travel = (1.0 - factor) * lever * one_minus_dist;
            const double axis_x = -lx * inv_half_x;
            const double axis_y = -ly * inv_half_y;
            double disp_x = axis_x * travel;
            double disp_y = axis_y * travel;

            if (distort > 0.0)
            {
                const double noise_x = vnotch::controllers::ValueNoise(lx * 0.055, ly * 0.055) * 2.0 - 1.0;
                const double noise_y = vnotch::controllers::ValueNoise(lx * 0.055 + 19.7, ly * 0.055 + 43.1) * 2.0 - 1.0;
                const double distort_scale = distort * 5.0 * grazing;
                disp_x += noise_x * distort_scale;
                disp_y += noise_y * distort_scale;
            }

            base_x += disp_x;
            base_y += disp_y;

            const double ca_s = std::min(u_chroma * 2.0, 3.0) * grazing;
            const double slope = ray == factor ? (b - a) * lut_scale * (2.0 * depth) + 2.0 * one_minus_dist * dome_pull : 0.0;
            const double radial_step = 1.0 - lever * inv_rim * (1.0 - factor + one_minus_dist * slope);
            if (std::abs(radial_step) > 1.5)
            {
                const double spread = (std::abs(radial_step) - 1.0) * 0.25;
                const double dx = axis_x * spread, dy = axis_y * spread;
                vnotch::controllers::PackSampleCoord(base_x - dx, base_y - dy, src_w, max_x, max_y, &cell[0], &cell[1]);
                vnotch::controllers::PackSampleCoord(base_x + dx, base_y + dy, src_w, max_x, max_y, &cell[2], &cell[3]);
                cell[4] = FilteredFootprint;
                cell[5] = static_cast<int32_t>(std::nearbyint(256.0 * std::min(ca_s / spread, 1.0)));
                continue;
            }
            if (ca_s <= 0.01)
            {
                int32_t s_idx = 0, s_aux = 0;
                vnotch::controllers::PackSampleCoord(base_x, base_y, src_w, max_x, max_y, &s_idx, &s_aux);
                cell[0] = s_idx; cell[1] = s_aux;
                cell[2] = s_idx; cell[3] = s_aux;
                cell[4] = s_idx; cell[5] = s_aux;
                continue;
            }

            const double ca_x = axis_x * ca_s;
            const double ca_y = axis_y * ca_s;

            vnotch::controllers::PackSampleCoord(base_x + ca_x, base_y + ca_y, src_w, max_x, max_y, &cell[0], &cell[1]);
            vnotch::controllers::PackSampleCoord(base_x, base_y, src_w, max_x, max_y, &cell[2], &cell[3]);
            vnotch::controllers::PackSampleCoord(base_x - ca_x, base_y - ca_y, src_w, max_x, max_y, &cell[4], &cell[5]);
        }
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_refract_gather(
    const uint8_t* src,
    uint8_t* dst,
    const int32_t* refract_map,
    int32_t src_stride,
    int32_t start_pixel,
    int32_t end_pixel,
    uint8_t adjust_color,
    float sat_factor,
    float bright_add) noexcept
{
    if (!src || !dst || !refract_map || src_stride <= 0 || start_pixel < 0 || end_pixel < start_pixel)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (adjust_color == 0)
    {
        for (int32_t i = start_pixel; i < end_pixel; ++i)
        {
            const int32_t* m = refract_map + static_cast<size_t>(i) * 6;
            const int32_t idx_b = m[0];
            const int32_t aux_b = m[1];
            const int32_t idx_r = m[4];
            const int32_t aux_r = m[5];
            uint8_t* out_px = dst + (static_cast<size_t>(i) << 2);

            if (idx_b == idx_r && aux_b == aux_r)
            {
                if ((aux_b >> 2) == 0)
                {
                    const uint8_t* p00 = src + idx_b;
                    out_px[0] = p00[0];
                    out_px[1] = p00[1];
                    out_px[2] = p00[2];
                    out_px[3] = 255;
                    continue;
                }

                const int32_t step_x = (aux_b & 1) != 0 ? 4 : 0;
                const int32_t step_y = (aux_b & 2) != 0 ? src_stride : 0;
                const int32_t wx = (aux_b >> 2) & 0x1FF;
                const int32_t wy = (aux_b >> 12) & 0x1FF;
                const int32_t inv_wx = 256 - wx;
                const int32_t inv_wy = 256 - wy;

                const uint8_t* p00 = src + idx_b;
                const uint8_t* p10 = p00 + step_x;
                const uint8_t* p01 = p00 + step_y;
                const uint8_t* p11 = p01 + step_x;

                out_px[0] = static_cast<uint8_t>(((p00[0] * inv_wx + p10[0] * wx) * inv_wy + (p01[0] * inv_wx + p11[0] * wx) * wy) >> 16);
                out_px[1] = static_cast<uint8_t>(((p00[1] * inv_wx + p10[1] * wx) * inv_wy + (p01[1] * inv_wx + p11[1] * wx) * wy) >> 16);
                out_px[2] = static_cast<uint8_t>(((p00[2] * inv_wx + p10[2] * wx) * inv_wy + (p01[2] * inv_wx + p11[2] * wx) * wy) >> 16);
                out_px[3] = 255;
            }
            else if (idx_r == FilteredFootprint)
            {
                int b, g, r;
                ReadFootprint(src, m, src_stride, b, g, r);
                out_px[0] = static_cast<uint8_t>(b);
                out_px[1] = static_cast<uint8_t>(g);
                out_px[2] = static_cast<uint8_t>(r);
                out_px[3] = 255;
            }
            else
            {
                out_px[0] = static_cast<uint8_t>(vnotch::controllers::BilerpChannel(src, idx_b, aux_b, src_stride, 0));
                out_px[1] = static_cast<uint8_t>(vnotch::controllers::BilerpChannel(src, m[2], m[3], src_stride, 1));
                out_px[2] = static_cast<uint8_t>(vnotch::controllers::BilerpChannel(src, idx_r, aux_r, src_stride, 2));
                out_px[3] = 255;
            }
        }
        return VN_OK;
    }

    const float safe_sat = std::isfinite(sat_factor) ? sat_factor : 1.0f;
    const float safe_bright = std::isfinite(bright_add) ? bright_add : 0.0f;
    const float one_minus_sat = 1.0f - safe_sat;
    for (int32_t i = start_pixel; i < end_pixel; ++i)
    {
        const int32_t* m = refract_map + static_cast<size_t>(i) * 6;
        const int32_t idx_b = m[0];
        const int32_t aux_b = m[1];
        const int32_t idx_r = m[4];
        const int32_t aux_r = m[5];

        int32_t b, g, r;
        if (idx_b == idx_r && aux_b == aux_r)
        {
            if ((aux_b >> 2) == 0)
            {
                const uint8_t* p00 = src + idx_b;
                b = p00[0];
                g = p00[1];
                r = p00[2];
            }
            else
            {
                const int32_t step_x = (aux_b & 1) != 0 ? 4 : 0;
                const int32_t step_y = (aux_b & 2) != 0 ? src_stride : 0;
                const int32_t wx = (aux_b >> 2) & 0x1FF;
                const int32_t wy = (aux_b >> 12) & 0x1FF;
                const int32_t inv_wx = 256 - wx;
                const int32_t inv_wy = 256 - wy;

                const uint8_t* p00 = src + idx_b;
                const uint8_t* p10 = p00 + step_x;
                const uint8_t* p01 = p00 + step_y;
                const uint8_t* p11 = p01 + step_x;

                b = ((p00[0] * inv_wx + p10[0] * wx) * inv_wy + (p01[0] * inv_wx + p11[0] * wx) * wy) >> 16;
                g = ((p00[1] * inv_wx + p10[1] * wx) * inv_wy + (p01[1] * inv_wx + p11[1] * wx) * wy) >> 16;
                r = ((p00[2] * inv_wx + p10[2] * wx) * inv_wy + (p01[2] * inv_wx + p11[2] * wx) * wy) >> 16;
            }
        }
        else if (idx_r == FilteredFootprint)
        {
            ReadFootprint(src, m, src_stride, b, g, r);
        }
        else
        {
            b = vnotch::controllers::BilerpChannel(src, idx_b, aux_b, src_stride, 0);
            g = vnotch::controllers::BilerpChannel(src, m[2], m[3], src_stride, 1);
            r = vnotch::controllers::BilerpChannel(src, idx_r, aux_r, src_stride, 2);
        }

        const float lum = (static_cast<float>(77 * r + 150 * g + 29 * b) * (1.0f / 256.0f)) * one_minus_sat + safe_bright;
        const uint32_t pixel = vnotch::controllers::PackAdjustedBgra(b, g, r, safe_sat, lum);
        std::memcpy(dst + (static_cast<size_t>(i) << 2), &pixel, sizeof(pixel));
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_glass_compute_squircle_lut(
    double power,
    int32_t segments,
    double* out_pow_cos,
    double* out_pow_sin) noexcept
{
    if (!out_pow_cos || !out_pow_sin || segments <= 1)
    {
        return VN_ERR_INVALID_ARG;
    }

    const double safe_power = (std::isfinite(power) && power > 0.1) ? power : 2.0;
    const double exp_val = 2.0 / safe_power;
    constexpr double kHalfPi = 1.57079632679489661923;

    out_pow_cos[0] = 0.0;
    out_pow_sin[0] = 0.0;
    for (int32_t i = 1; i < segments; ++i)
    {
        const double angle = kHalfPi * static_cast<double>(i) / static_cast<double>(segments);
        out_pow_cos[i] = std::pow(std::cos(angle), exp_val);
        out_pow_sin[i] = std::pow(std::sin(angle), exp_val);
    }
    return VN_OK;
}


}
