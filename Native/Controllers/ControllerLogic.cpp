#include "../vnotch_common.h"
#include "../vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>

namespace vnotch::controllers
{

namespace
{

constexpr wchar_t ToLowerAscii(wchar_t c) noexcept
{
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c + (L'a' - L'A')) : c;
}

constexpr bool IsAsciiWhitespace(wchar_t c) noexcept
{
    return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n' || c == L'\v' || c == L'\f';
}

std::wstring_view TrimWhitespace(std::wstring_view sv) noexcept
{
    size_t start = 0;
    while (start < sv.size() && IsAsciiWhitespace(sv[start]))
    {
        ++start;
    }
    size_t end = sv.size();
    while (end > start && IsAsciiWhitespace(sv[end - 1]))
    {
        --end;
    }
    return sv.substr(start, end - start);
}

bool EqualsIgnoreCaseAscii(std::wstring_view a, std::wstring_view b) noexcept
{
    if (a.size() != b.size())
    {
        return false;
    }
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ToLowerAscii(a[i]) != ToLowerAscii(b[i]))
        {
            return false;
        }
    }
    return true;
}

bool TryParseInt32Strict(std::wstring_view sv, int32_t* out_val) noexcept
{
    sv = TrimWhitespace(sv);
    if (sv.empty() || sv.size() > 16)
    {
        return false;
    }
    char buf[20]{};
    for (size_t i = 0; i < sv.size(); ++i)
    {
        if (sv[i] > 0x7F)
        {
            return false;
        }
        buf[i] = static_cast<char>(sv[i]);
    }
    int32_t v = 0;
    auto [ptr, ec] = std::from_chars(buf, buf + sv.size(), v);
    if (ec != std::errc{} || ptr != buf + sv.size())
    {
        return false;
    }
    *out_val = v;
    return true;
}

bool TryParseDoubleStrict(std::wstring_view sv, double* out_val) noexcept
{
    sv = TrimWhitespace(sv);
    if (sv.empty() || sv.size() > 63)
    {
        return false;
    }
    char buf[64]{};
    for (size_t i = 0; i < sv.size(); ++i)
    {
        if (sv[i] > 0x7F)
        {
            return false;
        }
        buf[i] = static_cast<char>(sv[i]);
    }
    double v = 0.0;
    auto [ptr, ec] = std::from_chars(buf, buf + sv.size(), v);
    if (ec != std::errc{} || ptr != buf + sv.size())
    {
        return false;
    }
    *out_val = v;
    return true;
}

bool TryTicksFromSeconds(double total_seconds, int64_t* out_ticks) noexcept
{
    *out_ticks = 0;
    constexpr double kMaxMillis = static_cast<double>(std::numeric_limits<int64_t>::max() / 10000LL);
    if (!std::isfinite(total_seconds) || total_seconds <= 0.0)
    {
        return false;
    }
    double millis = total_seconds * 1000.0;
    if (!std::isfinite(millis) || millis >= kMaxMillis || millis <= 0.0)
    {
        return false;
    }
    double ticks_d = millis * 10000.0;
    if (!std::isfinite(ticks_d) || ticks_d < 1.0 || ticks_d >= static_cast<double>(std::numeric_limits<int64_t>::max()))
    {
        return false;
    }
    int64_t ticks = static_cast<int64_t>(std::llround(ticks_d));
    if (ticks <= 0)
    {
        return false;
    }
    *out_ticks = ticks;
    return true;
}

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

inline double RoundedRectSdf(
    double px, double py, double bx, double by, double top_radius, double bottom_radius) noexcept
{
    const double r = py < 0.0 ? top_radius : bottom_radius;
    const double qx = std::abs(px) - (bx - r);
    const double qy = (py < 0.0 ? -py : py) - (by - r);
    if (qx > 0.0 && qy > 0.0) return std::sqrt(qx * qx + qy * qy) - r;
    if (qx > 0.0) return qx - r;
    if (qy > 0.0) return qy - r;
    return std::max(qx - r, qy - r);
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

inline uint8_t ClampByteFloat(float v) noexcept
{
    if (!std::isfinite(v))
    {
        return 0u;
    }
    const float clamped = std::min(std::max(v, 0.0f), 255.0f);
    return static_cast<uint8_t>(static_cast<int32_t>(clamped + 0.5f));
}

} // namespace

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t __stdcall vn_glass_frame_hash_128(
    const uint8_t* data,
    int32_t length,
    uint64_t* out_lo,
    uint64_t* out_hi) noexcept
{
    if (!out_lo || !out_hi)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (!data || length <= 0)
    {
        *out_lo = 0ULL;
        *out_hi = 0ULL;
        return VN_OK;
    }

    constexpr uint64_t kPrimeLo = 0x9E3779B185EBCA87ULL;
    constexpr uint64_t kPrimeHi = 0xC2B2AE3D27D4EB4FULL;
    constexpr uint64_t kIdxPrime = 0x165667B19E3779F9ULL;

    uint64_t lo = 0xCBF29CE484222325ULL ^ static_cast<uint64_t>(length);
    uint64_t hi = 0x84222325CBF29CE4ULL ^ (static_cast<uint64_t>(length) * kPrimeLo);

    int32_t offset = 0;
    if (length >= 64 && vnotch::cpu::SupportsX64V3())
    {
        __m256i acc_lo0 = _mm256_set_epi64x(
            static_cast<int64_t>(lo ^ (kPrimeLo * 1ULL)),
            static_cast<int64_t>(lo ^ (kPrimeLo * 3ULL)),
            static_cast<int64_t>(lo ^ (kPrimeLo * 5ULL)),
            static_cast<int64_t>(lo ^ (kPrimeLo * 7ULL)));
        __m256i acc_hi0 = _mm256_set_epi64x(
            static_cast<int64_t>(hi ^ (kPrimeHi * 1ULL)),
            static_cast<int64_t>(hi ^ (kPrimeHi * 3ULL)),
            static_cast<int64_t>(hi ^ (kPrimeHi * 5ULL)),
            static_cast<int64_t>(hi ^ (kPrimeHi * 7ULL)));
        __m256i acc_lo1 = _mm256_xor_si256(acc_lo0, _mm256_set1_epi64x(static_cast<int64_t>(kIdxPrime)));
        __m256i acc_hi1 = _mm256_xor_si256(acc_hi0, _mm256_set1_epi64x(static_cast<int64_t>(kPrimeLo)));

        const __m256i mult_lo = _mm256_set1_epi32(static_cast<int32_t>(0x85EBCA87u));
        const __m256i mult_hi = _mm256_set1_epi32(static_cast<int32_t>(0xC2B2AE3Du));
        __m256i idx_vec = _mm256_set_epi64x(4, 3, 2, 1);
        const __m256i idx_step = _mm256_set1_epi64x(static_cast<int64_t>(kIdxPrime));

        const int32_t limit64 = length - 63;
        for (; offset < limit64; offset += 64)
        {
            const __m256i v0 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(data + offset));
            const __m256i v1 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(data + offset + 32));

            const __m256i x0 = _mm256_xor_si256(v0, idx_vec);
            idx_vec = _mm256_add_epi64(idx_vec, idx_step);
            const __m256i x1 = _mm256_xor_si256(v1, idx_vec);
            idx_vec = _mm256_add_epi64(idx_vec, idx_step);

            const __m256i p0_lo = _mm256_mul_epu32(x0, mult_lo);
            const __m256i p0_hi = _mm256_mul_epu32(_mm256_srli_epi64(x0, 32), mult_hi);
            const __m256i p1_lo = _mm256_mul_epu32(x1, mult_lo);
            const __m256i p1_hi = _mm256_mul_epu32(_mm256_srli_epi64(x1, 32), mult_hi);

            acc_lo0 = _mm256_xor_si256(acc_lo0, _mm256_add_epi64(v0, p0_hi));
            acc_lo0 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_lo0, 31), _mm256_srli_epi64(acc_lo0, 33)),
                p0_lo);

            acc_hi0 = _mm256_xor_si256(acc_hi0, _mm256_add_epi64(x0, p0_lo));
            acc_hi0 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_hi0, 27), _mm256_srli_epi64(acc_hi0, 37)),
                p0_hi);

            acc_lo1 = _mm256_xor_si256(acc_lo1, _mm256_add_epi64(v1, p1_hi));
            acc_lo1 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_lo1, 31), _mm256_srli_epi64(acc_lo1, 33)),
                p1_lo);

            acc_hi1 = _mm256_xor_si256(acc_hi1, _mm256_add_epi64(x1, p1_lo));
            acc_hi1 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_hi1, 27), _mm256_srli_epi64(acc_hi1, 37)),
                p1_hi);
        }

        alignas(32) uint64_t lanes_lo0[4];
        alignas(32) uint64_t lanes_lo1[4];
        alignas(32) uint64_t lanes_hi0[4];
        alignas(32) uint64_t lanes_hi1[4];
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_lo0), acc_lo0);
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_lo1), acc_lo1);
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_hi0), acc_hi0);
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_hi1), acc_hi1);

        lo = vnotch::controllers::Avalanche64(lanes_lo0[0] ^ lanes_lo1[1]) ^
             vnotch::controllers::Avalanche64(lanes_lo0[2] ^ lanes_lo1[3]) ^
             vnotch::controllers::Avalanche64(lanes_lo1[0] ^ lanes_lo0[1]) ^
             vnotch::controllers::Avalanche64(lanes_lo1[2] ^ lanes_lo0[3]);
        hi = vnotch::controllers::Avalanche64(lanes_hi0[0] ^ lanes_hi1[1]) ^
             vnotch::controllers::Avalanche64(lanes_hi0[2] ^ lanes_hi1[3]) ^
             vnotch::controllers::Avalanche64(lanes_hi1[0] ^ lanes_hi0[1]) ^
             vnotch::controllers::Avalanche64(lanes_hi1[2] ^ lanes_hi0[3]);
    }

    for (; offset <= length - 8; offset += 8)
    {
        uint64_t w = 0;
        std::memcpy(&w, data + offset, sizeof(w));
        uint64_t idx_mix = static_cast<uint64_t>((offset >> 3) + 1) * kIdxPrime;
        lo = _rotl64(lo ^ (w + idx_mix), 31) * kPrimeLo;
        hi = _rotl64(hi ^ (w ^ idx_mix), 27) * kPrimeHi;
    }

    for (; offset < length; ++offset)
    {
        uint64_t b = static_cast<uint64_t>(data[offset]) + 1ULL;
        uint64_t pos = static_cast<uint64_t>(offset + 1) * 0x27D4EB2F165667C5ULL;
        lo = vnotch::controllers::Avalanche64(lo ^ (b * pos)) * kPrimeLo;
        hi = vnotch::controllers::Avalanche64(hi ^ ((b << 8) ^ pos)) * kPrimeHi;
    }

    *out_lo = vnotch::controllers::Avalanche64(lo ^ hi);
    *out_hi = vnotch::controllers::Avalanche64(hi ^ (lo * kPrimeLo));
    return VN_OK;
}

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
        !std::isfinite(params->refraction_d))
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
    const double aspect = std::clamp(
        static_cast<double>(params->notch_h) / std::max(static_cast<double>(params->notch_w), 1.0) * 2.5,
        0.0,
        1.0);
    const double vertical_balance = 0.68 + 0.32 * aspect;
    const double bend = std::max(params->edge_bend, 0.1);
    const double inv_half_x = 1.0 / std::max(half_x, 1.0);
    const double inv_half_y = 1.0 / std::max(half_y, 1.0);
    const double inv_min_half = 1.0 / std::max(min_half, 1.0);
    const double lut_scale = static_cast<double>(lut_len - 1);
    const int32_t lut_max_idx = lut_len - 1;

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

            const double inside = -vnotch::controllers::RoundedRectSdf(lx, ly, half_x, half_y, top_r, bottom_r);
            if (!std::isfinite(inside) || inside <= 0.0)
            {
                int32_t s_idx = 0, s_aux = 0;
                vnotch::controllers::PackSampleCoord(base_x, base_y, src_w, max_x, max_y, &s_idx, &s_aux);
                cell[0] = s_idx; cell[1] = s_aux;
                cell[2] = s_idx; cell[3] = s_aux;
                cell[4] = s_idx; cell[5] = s_aux;
                continue;
            }

            const double px_norm = lx * inv_half_x;
            const double py_norm = ly * inv_half_y;
            const double raw_dist = inside * inv_min_half;
            const double dist_norm = std::isfinite(raw_dist) ? std::clamp(raw_dist, 0.0, 1.0) : 0.0;
            const double one_minus_dist = 1.0 - dist_norm;

            const double raw_lut_pos = dist_norm * lut_scale;
            const double lut_pos = std::isfinite(raw_lut_pos)
                ? std::clamp(raw_lut_pos, 0.0, static_cast<double>(lut_max_idx - 1))
                : 0.0;
            int32_t lut_idx = static_cast<int32_t>(lut_pos);
            lut_idx = std::clamp(lut_idx, 0, lut_max_idx - 1);
            const double lut0 = std::isfinite(lut[lut_idx]) ? lut[lut_idx] : 1.0;
            const double lut1 = std::isfinite(lut[lut_idx + 1]) ? lut[lut_idx + 1] : lut0;
            const double frac = std::clamp((dist_norm * lut_scale) - static_cast<double>(lut_idx), 0.0, 1.0);
            const double refract_factor = lut0 + (lut1 - lut0) * frac;

            double disp_x = (px_norm * refract_factor - px_norm) * half_x * bend;
            double disp_y = (py_norm * refract_factor - py_norm) * half_y * bend;

            if (distort > 0.0 && one_minus_dist > 0.0)
            {
                const double noise_x = vnotch::controllers::ValueNoise(lx * 0.045, ly * 0.045) * 2.0 - 1.0;
                const double noise_y = vnotch::controllers::ValueNoise(lx * 0.045 + 19.7, ly * 0.045 + 43.1) * 2.0 - 1.0;
                const double distort_scale = distort * 2.25 * one_minus_dist;
                disp_x += noise_x * distort_scale;
                disp_y += noise_y * vertical_balance * distort_scale;
            }

            base_x += disp_x;
            base_y += disp_y;

            const double ca_s = std::min(u_chroma * one_minus_dist * 3.0, 8.0);
            if (ca_s <= 0.0)
            {
                int32_t s_idx = 0, s_aux = 0;
                vnotch::controllers::PackSampleCoord(base_x, base_y, src_w, max_x, max_y, &s_idx, &s_aux);
                cell[0] = s_idx; cell[1] = s_aux;
                cell[2] = s_idx; cell[3] = s_aux;
                cell[4] = s_idx; cell[5] = s_aux;
                continue;
            }

            const double disp_len = std::sqrt(disp_x * disp_x + disp_y * disp_y);
            const double ca_dir_x = (std::isfinite(disp_len) && disp_len > 1e-6) ? disp_x / disp_len : 0.0;
            const double ca_dir_y = (std::isfinite(disp_len) && disp_len > 1e-6) ? disp_y / disp_len : 0.0;
            const double ca_x = ca_dir_x * ca_s;
            const double ca_y = ca_dir_y * vertical_balance * ca_s;

            vnotch::controllers::PackSampleCoord(base_x - ca_x, base_y - ca_y, src_w, max_x, max_y, &cell[0], &cell[1]);
            vnotch::controllers::PackSampleCoord(base_x, base_y, src_w, max_x, max_y, &cell[2], &cell[3]);
            vnotch::controllers::PackSampleCoord(base_x + ca_x, base_y + ca_y, src_w, max_x, max_y, &cell[4], &cell[5]);
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
        else
        {
            b = vnotch::controllers::BilerpChannel(src, idx_b, aux_b, src_stride, 0);
            g = vnotch::controllers::BilerpChannel(src, m[2], m[3], src_stride, 1);
            r = vnotch::controllers::BilerpChannel(src, idx_r, aux_r, src_stride, 2);
        }

        const float lum = (static_cast<float>(77 * r + 150 * g + 29 * b) * (1.0f / 256.0f)) * one_minus_sat + safe_bright;
        uint8_t* out_px = dst + (static_cast<size_t>(i) << 2);
        out_px[0] = vnotch::controllers::ClampByteFloat(lum + static_cast<float>(b) * safe_sat);
        out_px[1] = vnotch::controllers::ClampByteFloat(lum + static_cast<float>(g) * safe_sat);
        out_px[2] = vnotch::controllers::ClampByteFloat(lum + static_cast<float>(r) * safe_sat);
        out_px[3] = 255;
    }

    return VN_OK;
}

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

VN_EXPORT int32_t __stdcall vn_calendar_clamp_index(
    int32_t idx,
    int32_t* out_clamped) noexcept
{
    if (!out_clamped) return VN_ERR_INVALID_ARG;
    constexpr int32_t kTotalDays = 11;
    *out_clamped = std::clamp(idx, 0, kTotalDays - 1);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_center_index_from_strip_x(
    double strip_x,
    int32_t* out_center_idx) noexcept
{
    if (!out_center_idx) return VN_ERR_INVALID_ARG;
    constexpr int32_t kTotalDays = 11;
    constexpr double kCellWidth = 30.0;
    if (!std::isfinite(strip_x)) strip_x = 0.0;
    double raw = std::clamp(std::nearbyint((30.0 - strip_x) / kCellWidth), 0.0, static_cast<double>(kTotalDays - 1));
    *out_center_idx = static_cast<int32_t>(raw);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_highlight_x_for_index(
    int32_t center_idx,
    double* out_highlight_x) noexcept
{
    if (!out_highlight_x) return VN_ERR_INVALID_ARG;
    constexpr double kCellWidth = 30.0;
    constexpr double kHighlightDiameter = 24.0;
    *out_highlight_x = static_cast<double>(center_idx) * kCellWidth + (kCellWidth - kHighlightDiameter) / 2.0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_strip_x_for_index(
    int32_t center_idx,
    double* out_strip_x) noexcept
{
    if (!out_strip_x) return VN_ERR_INVALID_ARG;
    constexpr double kCellWidth = 30.0;
    *out_strip_x = kCellWidth - (static_cast<double>(center_idx) * kCellWidth);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_calendar_compute_scroll_step(
    double accumulator,
    int32_t delta,
    int32_t current_center_idx,
    double* out_remaining,
    int32_t* out_new_idx,
    int32_t* out_old_idx,
    int32_t* out_step_count) noexcept
{
    if (!out_remaining || !out_new_idx || !out_old_idx || !out_step_count)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int32_t kTotalDays = 11;
    if (!std::isfinite(accumulator)) accumulator = 0.0;
    double acc = accumulator + static_cast<double>(delta);
    int32_t direction = acc > 0.0 ? -1 : 1;
    double abs_acc = std::abs(acc);
    int32_t step_count = static_cast<int32_t>(std::clamp(abs_acc / 120.0, 0.0, 1000.0));
    if (step_count == 0 && abs_acc >= 72.0)
    {
        step_count = 1;
    }

    if (step_count == 0)
    {
        *out_remaining = acc;
        *out_new_idx = current_center_idx;
        *out_old_idx = current_center_idx;
        *out_step_count = 0;
        return VN_OK;
    }

    double sign = acc > 0.0 ? 1.0 : (acc < 0.0 ? -1.0 : 0.0);
    *out_remaining = acc - sign * static_cast<double>(step_count) * 120.0;
    *out_old_idx = current_center_idx;
    *out_new_idx = std::clamp(current_center_idx + (direction * step_count), 0, kTotalDays - 1);
    *out_step_count = step_count;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_is_alt_space_key(
    uint32_t vk_code,
    uint32_t flags,
    uint8_t* out_match) noexcept
{
    if (!out_match) return VN_ERR_INVALID_ARG;
    constexpr uint32_t kVkSpace = 0x20u;
    constexpr uint32_t kLlkhfAltDown = 0x20u;
    *out_match = (vk_code == kVkSpace && (flags & kLlkhfAltDown) != 0u) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_is_escape_key(
    uint32_t vk_code,
    uint8_t* out_match) noexcept
{
    if (!out_match) return VN_ERR_INVALID_ARG;
    *out_match = (vk_code == 0x1Bu) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_should_dispatch_fallback_toggle(
    uint8_t space_already_down,
    uint32_t last_space_time,
    uint32_t current_time,
    uint8_t* out_dispatch) noexcept
{
    if (!out_dispatch) return VN_ERR_INVALID_ARG;
    constexpr uint32_t kStaleFallbackKeyDownMs = 500u;
    uint32_t elapsed = current_time - last_space_time;
    *out_dispatch = (space_already_down == 0 || elapsed > kStaleFallbackKeyDownMs) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_kbd_process(
    uint8_t enabled,
    uint8_t fallback,
    uint8_t open,
    int32_t escape_session,
    uint8_t* io_has_observed,
    uint8_t* io_obs_enabled,
    uint8_t* io_obs_fallback,
    int32_t* io_obs_escape_session,
    uint8_t* io_space_down,
    uint8_t* io_escape_down,
    uint32_t* io_last_space_time,
    uint32_t vk_code,
    uint32_t flags,
    uint32_t time,
    int32_t message,
    int32_t* out_action) noexcept
{
    if (!io_has_observed || !io_obs_enabled || !io_obs_fallback || !io_obs_escape_session ||
        !io_space_down || !io_escape_down || !io_last_space_time || !out_action)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (*io_has_observed == 0 || *io_obs_escape_session != escape_session)
    {
        *io_escape_down = 0;
    }
    if (*io_has_observed == 0 || *io_obs_enabled != enabled || *io_obs_fallback != fallback)
    {
        *io_space_down = 0;
        *io_last_space_time = 0;
    }

    *io_has_observed = 1;
    *io_obs_enabled = enabled;
    *io_obs_fallback = fallback;
    *io_obs_escape_session = escape_session;

    if (enabled == 0)
    {
        *out_action = 0; // None
        return VN_OK;
    }

    constexpr int32_t kWmKeyDown = 0x0100;
    constexpr int32_t kWmKeyUp = 0x0101;
    constexpr int32_t kWmSysKeyDown = 0x0104;
    constexpr int32_t kWmSysKeyUp = 0x0105;
    constexpr uint32_t kVkSpace = 0x20u;
    constexpr uint32_t kLlkhfAltDown = 0x20u;
    constexpr uint32_t kStaleFallbackKeyDownMs = 500u;

    bool is_key_up = (message == kWmKeyUp || message == kWmSysKeyUp);
    bool is_key_down = (message == kWmKeyDown || message == kWmSysKeyDown);

    if (vk_code == 0x1Bu)
    {
        if (*io_escape_down != 0 && is_key_up)
        {
            *io_escape_down = 0;
            *out_action = 1; // Suppress
            return VN_OK;
        }
        if (open != 0 && is_key_down)
        {
            if (*io_escape_down != 0)
            {
                *out_action = 1; // Suppress
                return VN_OK;
            }
            *io_escape_down = 1;
            *out_action = 3; // Escape
            return VN_OK;
        }
    }

    if (*io_space_down != 0 && vk_code == kVkSpace && is_key_up)
    {
        *io_space_down = 0;
        *out_action = 1; // Suppress
        return VN_OK;
    }

    if (fallback != 0 && vk_code == kVkSpace && (flags & kLlkhfAltDown) != 0u && is_key_down)
    {
        bool dispatch = (*io_space_down == 0) || ((time - *io_last_space_time) > kStaleFallbackKeyDownMs);
        *io_space_down = 1;
        *io_last_space_time = time;
        *out_action = dispatch ? 2 : 1; // 2 = Toggle, 1 = Suppress
        return VN_OK;
    }

    *out_action = 0; // None
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_hotkey_parse_modifiers(
    const wchar_t* text,
    int32_t text_len,
    uint32_t* out_modifiers,
    int32_t* out_key_start,
    int32_t* out_key_len,
    uint8_t* out_valid) noexcept
{
    if (!out_modifiers || !out_key_start || !out_key_len || !out_valid)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_modifiers = 0;
    *out_key_start = 0;
    *out_key_len = 0;
    *out_valid = 0;

    if (!text || text_len <= 0)
    {
        return VN_OK;
    }

    std::wstring_view full(text, static_cast<size_t>(text_len));
    std::wstring_view tokens[8]{};
    int32_t token_count = 0;

    size_t pos = 0;
    while (pos < full.size())
    {
        size_t plus = full.find(L'+', pos);
        std::wstring_view raw = (plus == std::wstring_view::npos)
            ? full.substr(pos)
            : full.substr(pos, plus - pos);
        std::wstring_view trimmed = vnotch::controllers::TrimWhitespace(raw);
        if (!trimmed.empty())
        {
            if (token_count >= 8)
            {
                return VN_OK;
            }
            tokens[token_count++] = trimmed;
        }
        if (plus == std::wstring_view::npos)
        {
            break;
        }
        pos = plus + 1;
    }

    if (token_count < 2)
    {
        return VN_OK;
    }

    uint32_t mods = 0;
    for (int32_t i = 0; i < token_count - 1; ++i)
    {
        std::wstring_view part = tokens[i];
        uint32_t flag = 0;
        if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"ctrl") ||
            vnotch::controllers::EqualsIgnoreCaseAscii(part, L"control"))
        {
            flag = 2u;
        }
        else if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"shift"))
        {
            flag = 4u;
        }
        else if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"alt"))
        {
            flag = 1u;
        }
        else if (vnotch::controllers::EqualsIgnoreCaseAscii(part, L"win"))
        {
            flag = 8u;
        }

        if (flag == 0u || (mods & flag) != 0u)
        {
            return VN_OK;
        }
        mods |= flag;
    }

    if ((mods & (1u | 2u | 8u)) == 0u)
    {
        return VN_OK;
    }

    std::wstring_view last_token = tokens[token_count - 1];
    // Reject purely numeric tokens like "999999" that Enum.TryParse<Key> would accept as arbitrary enum integers
    bool all_digits = !last_token.empty();
    for (wchar_t c : last_token)
    {
        if (c < L'0' || c > L'9')
        {
            all_digits = false;
            break;
        }
    }
    if (all_digits && last_token.size() > 1)
    {
        return VN_OK;
    }

    *out_modifiers = mods;
    *out_key_start = static_cast<int32_t>(last_token.data() - text);
    *out_key_len = static_cast<int32_t>(last_token.size());
    *out_valid = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_hotkey_is_os_reserved(
    uint32_t modifiers,
    uint32_t vk,
    uint8_t* out_reserved) noexcept
{
    if (!out_reserved) return VN_ERR_INVALID_ARG;
    uint32_t mods = modifiers & ~0x4000u;
    bool reserved =
        (mods == 8u && vk == 0x56u) ||
        (mods == (8u | 4u) && vk == 0x53u) ||
        (mods == 8u && vk == 0x09u) ||
        (mods == 8u && vk == 0x2Cu);
    *out_reserved = reserved ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gesture_evaluate_move(
    double start_x,
    double start_y,
    double current_x,
    double current_y,
    double elapsed_since_last_swipe_ms,
    double* out_delta_x,
    double* out_delta_y,
    int32_t* out_gesture_kind) noexcept
{
    if (!out_delta_x || !out_delta_y || !out_gesture_kind)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr double kHorizontalSwipeThreshold = 40.0;
    constexpr double kVerticalSwipeThreshold = 35.0;
    constexpr double kDirectionLockRatio = 1.6;
    constexpr double kSwipeCooldownMs = 400.0;
    constexpr double kDeadZone = 4.0;

    double dx = current_x - start_x;
    double dy = current_y - start_y;
    *out_delta_x = dx;
    *out_delta_y = dy;
    *out_gesture_kind = 0;

    double abs_x = std::abs(dx);
    double abs_y = std::abs(dy);
    if (abs_x < kDeadZone && abs_y < kDeadZone)
    {
        return VN_OK;
    }
    if (elapsed_since_last_swipe_ms < kSwipeCooldownMs)
    {
        return VN_OK;
    }

    if (abs_x >= kHorizontalSwipeThreshold && abs_x > abs_y * kDirectionLockRatio)
    {
        *out_gesture_kind = (dx < 0.0) ? 1 : 2; // 1 = SwipeLeft, 2 = SwipeRight
        return VN_OK;
    }

    if (abs_y >= kVerticalSwipeThreshold && dy > 0.0 && abs_y > abs_x * kDirectionLockRatio)
    {
        *out_gesture_kind = 3; // 3 = SwipeDown
        return VN_OK;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_gesture_evaluate_end(
    double start_x,
    double start_y,
    double end_x,
    double end_y,
    double elapsed_since_last_tap_ms,
    uint8_t* out_is_tap,
    uint8_t* out_is_double_tap) noexcept
{
    if (!out_is_tap || !out_is_double_tap)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr double kDeadZone = 4.0;
    constexpr double kDoubleTapMaxMs = 350.0;

    double total_movement = std::abs(end_x - start_x) + std::abs(end_y - start_y);
    if (total_movement < kDeadZone * 2.0)
    {
        *out_is_tap = 1;
        *out_is_double_tap = (elapsed_since_last_tap_ms >= 0.0 && elapsed_since_last_tap_ms <= kDoubleTapMaxMs) ? 1 : 0;
    }
    else
    {
        *out_is_tap = 0;
        *out_is_double_tap = 0;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_parse_custom_time(
    const wchar_t* input,
    int32_t input_len,
    int64_t* out_ticks,
    uint8_t* out_valid) noexcept
{
    if (!out_ticks || !out_valid)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ticks = 0;
    *out_valid = 0;

    if (!input || input_len <= 0)
    {
        return VN_OK;
    }

    std::wstring_view s = vnotch::controllers::TrimWhitespace(
        std::wstring_view(input, static_cast<size_t>(input_len)));
    if (s.empty())
    {
        return VN_OK;
    }

    if (s.find(L':') != std::wstring_view::npos)
    {
        std::wstring_view parts[4]{};
        int32_t part_count = 0;
        size_t pos = 0;
        while (pos <= s.size())
        {
            if (part_count >= 4)
            {
                part_count = 5;
                break;
            }
            size_t colon = s.find(L':', pos);
            if (colon == std::wstring_view::npos)
            {
                parts[part_count++] = s.substr(pos);
                break;
            }
            parts[part_count++] = s.substr(pos, colon - pos);
            pos = colon + 1;
        }

        if (part_count == 2)
        {
            int32_t mins = 0, secs = 0;
            if (vnotch::controllers::TryParseInt32Strict(parts[0], &mins) &&
                vnotch::controllers::TryParseInt32Strict(parts[1], &secs) &&
                mins >= 0 && secs >= 0 && secs < 60)
            {
                double total_sec = static_cast<double>(mins) * 60.0 + static_cast<double>(secs);
                if (vnotch::controllers::TryTicksFromSeconds(total_sec, out_ticks))
                {
                    *out_valid = 1;
                }
                return VN_OK;
            }
        }
        else if (part_count == 3)
        {
            int32_t hrs = 0, mins = 0, secs = 0;
            if (vnotch::controllers::TryParseInt32Strict(parts[0], &hrs) &&
                vnotch::controllers::TryParseInt32Strict(parts[1], &mins) &&
                vnotch::controllers::TryParseInt32Strict(parts[2], &secs) &&
                hrs >= 0 && mins >= 0 && mins < 60 && secs >= 0 && secs < 60)
            {
                double total_sec = static_cast<double>(hrs) * 3600.0 +
                                   static_cast<double>(mins) * 60.0 +
                                   static_cast<double>(secs);
                if (vnotch::controllers::TryTicksFromSeconds(total_sec, out_ticks))
                {
                    *out_valid = 1;
                }
                return VN_OK;
            }
        }
        return VN_OK;
    }

    double plain_minutes = 0.0;
    if (vnotch::controllers::TryParseDoubleStrict(s, &plain_minutes))
    {
        if (vnotch::controllers::TryTicksFromSeconds(plain_minutes * 60.0, out_ticks))
        {
            *out_valid = 1;
        }
        return VN_OK;
    }

    double total_seconds = 0.0;
    bool any_match = false;
    size_t i = 0;
    while (i < s.size())
    {
        while (i < s.size() && !(s[i] >= L'0' && s[i] <= L'9'))
        {
            ++i;
        }
        if (i >= s.size())
        {
            break;
        }
        size_t num_start = i;
        while (i < s.size() && (s[i] >= L'0' && s[i] <= L'9'))
        {
            ++i;
        }
        if (i < s.size() && s[i] == L'.')
        {
            size_t dot_pos = i++;
            size_t frac_start = i;
            while (i < s.size() && (s[i] >= L'0' && s[i] <= L'9'))
            {
                ++i;
            }
            if (i == frac_start)
            {
                i = dot_pos;
            }
        }
        std::wstring_view num_sv = s.substr(num_start, i - num_start);
        while (i < s.size() && vnotch::controllers::IsAsciiWhitespace(s[i]))
        {
            ++i;
        }
        if (i < s.size())
        {
            wchar_t unit = vnotch::controllers::ToLowerAscii(s[i]);
            if (unit == L's' || unit == L'm' || unit == L'h' || unit == L'd')
            {
                double val = 0.0;
                if (vnotch::controllers::TryParseDoubleStrict(num_sv, &val) && std::isfinite(val))
                {
                    any_match = true;
                    switch (unit)
                    {
                    case L's': total_seconds += val; break;
                    case L'm': total_seconds += val * 60.0; break;
                    case L'h': total_seconds += val * 3600.0; break;
                    case L'd': total_seconds += val * 86400.0; break;
                    default: break;
                    }
                }
                ++i;
            }
        }
    }

    if (any_match && vnotch::controllers::TryTicksFromSeconds(total_seconds, out_ticks))
    {
        *out_valid = 1;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_format_remaining(
    int64_t remaining_ticks,
    wchar_t* out_buf,
    int32_t buf_cap,
    int32_t* out_len) noexcept
{
    if (!out_buf || buf_cap <= 0 || !out_len)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kTicksPerSecond = 10000000LL;
    constexpr int64_t kTicksPerMinute = 60LL * kTicksPerSecond;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;
    constexpr int64_t kTicksPerDay = 24LL * kTicksPerHour;

    int64_t ticks = std::clamp<int64_t>(remaining_ticks, 0LL, 99999LL * kTicksPerHour);
    int32_t written = 0;

    if (ticks >= kTicksPerDay)
    {
        int32_t total_days = static_cast<int32_t>(ticks / kTicksPerDay);
        int32_t hours = static_cast<int32_t>((ticks % kTicksPerDay) / kTicksPerHour);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%dd %02dh", total_days, hours);
    }
    else if (ticks >= kTicksPerHour)
    {
        int32_t total_hours = static_cast<int32_t>(ticks / kTicksPerHour);
        int32_t minutes = static_cast<int32_t>((ticks % kTicksPerHour) / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%02d:%02d:%02d", total_hours, minutes, seconds);
    }
    else
    {
        int32_t total_minutes = static_cast<int32_t>(ticks / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%02d:%02d", total_minutes, seconds);
    }

    *out_len = (written > 0) ? written : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_compute_adjust_ticks(
    int64_t current_duration_ticks,
    int32_t direction,
    int64_t* out_next_ticks,
    uint8_t* out_changed) noexcept
{
    if (!out_next_ticks || !out_changed)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (direction == 0)
    {
        *out_next_ticks = current_duration_ticks;
        *out_changed = 0;
        return VN_OK;
    }

    constexpr int64_t kTicksPerMinute = 60LL * 10000000LL;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;
    constexpr int64_t kTicksPerDay = 24LL * kTicksPerHour;
    constexpr int64_t kMinimumTicks = kTicksPerMinute;
    constexpr int64_t kMaximumTicks = 7LL * kTicksPerDay;

    int64_t clamped_current = std::clamp<int64_t>(current_duration_ticks, 0LL, kMaximumTicks);
    int64_t step = (clamped_current >= kTicksPerDay)
        ? kTicksPerHour
        : ((clamped_current >= kTicksPerHour) ? (5LL * kTicksPerMinute) : kTicksPerMinute);

    int64_t next = clamped_current + (direction > 0 ? step : -step);
    next = std::clamp(next, kMinimumTicks, kMaximumTicks);
    *out_next_ticks = next;
    *out_changed = (next != current_duration_ticks) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_compute_progress(
    int64_t duration_ticks,
    int64_t remaining_ticks,
    double* out_progress) noexcept
{
    if (!out_progress)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (duration_ticks <= 0)
    {
        *out_progress = 0.0;
        return VN_OK;
    }
    double ratio = static_cast<double>(remaining_ticks) / static_cast<double>(duration_ticks);
    *out_progress = 1.0 - std::clamp(ratio, 0.0, 1.0);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_progress_interpolate_ticks(
    int64_t last_position_ticks,
    int64_t duration_ticks,
    int64_t elapsed_ticks,
    double playback_rate,
    uint8_t is_playing,
    uint8_t is_throttled,
    uint8_t is_web_or_youtube,
    int64_t* out_display_ticks,
    double* out_ratio) noexcept
{
    if (!out_display_ticks || !out_ratio)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (duration_ticks <= 0)
    {
        *out_display_ticks = 0;
        *out_ratio = 0.0;
        return VN_OK;
    }

    int64_t display = last_position_ticks;
    if (is_playing != 0)
    {
        constexpr int64_t kTicksPerSecond = 10000000LL;
        int64_t cap_seconds = (is_throttled != 0)
            ? 3600LL
            : ((is_web_or_youtube != 0) ? 600LL : 30LL);
        int64_t max_elapsed = cap_seconds * kTicksPerSecond;
        int64_t clamped_elapsed = std::min(elapsed_ticks, max_elapsed);
        if (clamped_elapsed > 0)
        {
            const double rate = (std::isfinite(playback_rate) && playback_rate >= 0.0) ? std::min(playback_rate, 16.0) : 1.0;
            display += static_cast<int64_t>(static_cast<double>(clamped_elapsed) * rate);
        }
    }

    display = std::clamp<int64_t>(display, 0LL, duration_ticks);
    *out_display_ticks = display;
    *out_ratio = static_cast<double>(display) / static_cast<double>(duration_ticks);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_progress_format_time(
    int64_t time_ticks,
    wchar_t* out_buf,
    int32_t buf_cap,
    int32_t* out_len) noexcept
{
    if (!out_buf || buf_cap <= 0 || !out_len)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kTicksPerSecond = 10000000LL;
    constexpr int64_t kTicksPerMinute = 60LL * kTicksPerSecond;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;

    int64_t ticks = std::clamp<int64_t>(time_ticks, 0LL, 99999LL * kTicksPerHour);
    int32_t written = 0;

    if (ticks >= kTicksPerHour)
    {
        int32_t hours = static_cast<int32_t>(ticks / kTicksPerHour);
        int32_t minutes = static_cast<int32_t>((ticks % kTicksPerHour) / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%d:%02d:%02d", hours, minutes, seconds);
    }
    else
    {
        int32_t minutes = static_cast<int32_t>(ticks / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%d:%02d", minutes, seconds);
    }

    *out_len = (written > 0) ? written : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_audio_mixer_select_icon_glyph(
    float volume,
    uint8_t muted,
    int32_t* out_codepoint) noexcept
{
    if (!out_codepoint)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (muted != 0 || !std::isfinite(volume) || volume <= 0.01f)
    {
        *out_codepoint = 0xE74F;
    }
    else if (volume < 0.33f)
    {
        *out_codepoint = 0xE993;
    }
    else if (volume < 0.66f)
    {
        *out_codepoint = 0xE994;
    }
    else
    {
        *out_codepoint = 0xE995;
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

} // extern "C"
