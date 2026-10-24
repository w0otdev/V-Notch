#include "../vnotch_common.h"
#include <immintrin.h>
#include <algorithm>
#include <cstdint>

namespace vnotch::controllers
{

class GlassBoxBlur final
{
    struct ExactByteAverage
    {
        uint32_t divisor;
        uint64_t reciprocal;

        constexpr explicit ExactByteAverage(int32_t d) noexcept
            : divisor(static_cast<uint32_t>(d))
            , reciprocal(((1ULL << 32) + static_cast<uint32_t>(d) - 1ULL) / static_cast<uint32_t>(d))
        {
        }

        __forceinline uint8_t divide(int32_t sum) const noexcept
        {
            uint32_t u = static_cast<uint32_t>(sum);
            uint32_t q = static_cast<uint32_t>((static_cast<uint64_t>(u) * reciprocal) >> 32);
            if (q * divisor > u)
            {
                --q;
            }
            return static_cast<uint8_t>(q);
        }
    };

    static __forceinline void widen_u8x32_to_i32x8(
        const uint8_t* ptr,
        __m256i& a,
        __m256i& b,
        __m256i& c,
        __m256i& d) noexcept
    {
        __m128i bytes_lo = _mm_loadu_si128(reinterpret_cast<const __m128i*>(ptr));
        __m128i bytes_hi = _mm_loadu_si128(reinterpret_cast<const __m128i*>(ptr + 16));
        __m256i words_lo = _mm256_cvtepu8_epi16(bytes_lo);
        __m256i words_hi = _mm256_cvtepu8_epi16(bytes_hi);

        a = _mm256_cvtepi16_epi32(_mm256_castsi256_si128(words_lo));
        b = _mm256_cvtepi16_epi32(_mm256_extracti128_si256(words_lo, 1));
        c = _mm256_cvtepi16_epi32(_mm256_castsi256_si128(words_hi));
        d = _mm256_cvtepi16_epi32(_mm256_extracti128_si256(words_hi, 1));
    }

    static __forceinline __m256i average_i32x8(__m256i sum, __m256 divisor) noexcept
    {
        return _mm256_cvttps_epi32(_mm256_div_ps(_mm256_cvtepi32_ps(sum), divisor));
    }

    static __forceinline __m256i narrow_i32x8_to_i16x16(__m256i lo, __m256i hi) noexcept
    {
        __m256i packed = _mm256_packs_epi32(lo, hi);
        return _mm256_permute4x64_epi64(packed, _MM_SHUFFLE(3, 1, 2, 0));
    }

    static __forceinline void narrow_and_store_u8x32(
        uint8_t* dst,
        __m256i s0,
        __m256i s1,
        __m256i s2,
        __m256i s3,
        __m256 divisor) noexcept
    {
        __m256i lo16 = narrow_i32x8_to_i16x16(average_i32x8(s0, divisor), average_i32x8(s1, divisor));
        __m256i hi16 = narrow_i32x8_to_i16x16(average_i32x8(s2, divisor), average_i32x8(s3, divisor));
        __m256i mask = _mm256_set1_epi16(0x00FF);
        __m256i packed8 = _mm256_packus_epi16(_mm256_and_si256(lo16, mask), _mm256_and_si256(hi16, mask));
        __m256i ordered8 = _mm256_permute4x64_epi64(packed8, _MM_SHUFFLE(3, 1, 2, 0));
        _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst), ordered8);
    }

public:
    static int32_t Horizontal(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t radius,
        int32_t y0,
        int32_t y1) noexcept
    {
        if (src == nullptr || dst == nullptr || w <= 0 || radius < 0 || y0 < 0 || y1 < y0)
        {
            return VN_ERR_INVALID_ARG;
        }

        ExactByteAverage divisor(2 * radius + 1);
        int32_t left_end = std::min(w, radius);
        int32_t right_start = std::max(left_end, w - radius - 1);

        for (int32_t y = y0; y < y1; ++y)
        {
            int32_t row_base = y * w * 4;
            int32_t sum_b = 0, sum_g = 0, sum_r = 0, sum_a = 0;
            for (int32_t dx = -radius; dx <= radius; ++dx)
            {
                int32_t nx = std::clamp(dx, 0, w - 1);
                int32_t o = row_base + nx * 4;
                sum_b += src[o];
                sum_g += src[o + 1];
                sum_r += src[o + 2];
                sum_a += src[o + 3];
            }

            int32_t x = 0;
            for (; x < left_end; ++x)
            {
                int32_t t = row_base + x * 4;
                dst[t] = divisor.divide(sum_b);
                dst[t + 1] = divisor.divide(sum_g);
                dst[t + 2] = divisor.divide(sum_r);
                dst[t + 3] = divisor.divide(sum_a);

                int32_t in_x = std::min(w - 1, x + 1 + radius);
                int32_t o_out = row_base;
                int32_t o_in = row_base + in_x * 4;
                sum_b += src[o_in] - src[o_out];
                sum_g += src[o_in + 1] - src[o_out + 1];
                sum_r += src[o_in + 2] - src[o_out + 2];
                sum_a += src[o_in + 3] - src[o_out + 3];
            }

            for (; x < right_start; ++x)
            {
                int32_t t = row_base + x * 4;
                dst[t] = divisor.divide(sum_b);
                dst[t + 1] = divisor.divide(sum_g);
                dst[t + 2] = divisor.divide(sum_r);
                dst[t + 3] = divisor.divide(sum_a);

                int32_t o_out = t - radius * 4;
                int32_t o_in = t + (radius + 1) * 4;
                sum_b += src[o_in] - src[o_out];
                sum_g += src[o_in + 1] - src[o_out + 1];
                sum_r += src[o_in + 2] - src[o_out + 2];
                sum_a += src[o_in + 3] - src[o_out + 3];
            }

            int32_t last_pixel_offset = row_base + (w - 1) * 4;
            for (; x < w; ++x)
            {
                int32_t t = row_base + x * 4;
                dst[t] = divisor.divide(sum_b);
                dst[t + 1] = divisor.divide(sum_g);
                dst[t + 2] = divisor.divide(sum_r);
                dst[t + 3] = divisor.divide(sum_a);

                int32_t out_x = std::max(0, x - radius);
                int32_t o_out = row_base + out_x * 4;
                int32_t o_in = last_pixel_offset;
                sum_b += src[o_in] - src[o_out];
                sum_g += src[o_in + 1] - src[o_out + 1];
                sum_r += src[o_in + 2] - src[o_out + 2];
                sum_a += src[o_in + 3] - src[o_out + 3];
            }
        }

        return VN_OK;
    }

    static int32_t Vertical(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t h,
        int32_t radius,
        int32_t x0,
        int32_t x1) noexcept
    {
        if (src == nullptr || dst == nullptr || w <= 0 || h <= 0 || radius < 0 || x0 < 0 || x1 < x0)
        {
            return VN_ERR_INVALID_ARG;
        }

        int32_t window = 2 * radius + 1;
        int32_t stride = w * 4;
        int32_t column = x0 * 4;
        int32_t end = x1 * 4;
        int32_t top_end = std::min(h, radius);
        int32_t bottom_start = std::max(top_end, h - radius - 1);
        int32_t last_row_offset = (h - 1) * stride;

        if (window <= 4097)
        {
            __m256 divisor = _mm256_set1_ps(static_cast<float>(window));
            for (; column <= end - 32; column += 32)
            {
                __m256i s0 = _mm256_setzero_si256();
                __m256i s1 = s0;
                __m256i s2 = s0;
                __m256i s3 = s0;

                for (int32_t dy = -radius; dy <= radius; ++dy)
                {
                    __m256i a, b, c, d;
                    widen_u8x32_to_i32x8(src + static_cast<size_t>(std::clamp(dy, 0, h - 1)) * stride + column, a, b, c, d);
                    s0 = _mm256_add_epi32(s0, a);
                    s1 = _mm256_add_epi32(s1, b);
                    s2 = _mm256_add_epi32(s2, c);
                    s3 = _mm256_add_epi32(s3, d);
                }

                __m256i top_a, top_b, top_c, top_d;
                __m256i bot_a, bot_b, bot_c, bot_d;
                widen_u8x32_to_i32x8(src + column, top_a, top_b, top_c, top_d);
                widen_u8x32_to_i32x8(src + last_row_offset + column, bot_a, bot_b, bot_c, bot_d);

                int32_t y = 0;
                for (; y < top_end; ++y)
                {
                    narrow_and_store_u8x32(dst + static_cast<size_t>(y) * stride + column, s0, s1, s2, s3, divisor);
                    __m256i a, b, c, d;
                    widen_u8x32_to_i32x8(src + static_cast<size_t>(std::min(h - 1, y + radius + 1)) * stride + column, a, b, c, d);
                    s0 = _mm256_sub_epi32(_mm256_add_epi32(s0, a), top_a);
                    s1 = _mm256_sub_epi32(_mm256_add_epi32(s1, b), top_b);
                    s2 = _mm256_sub_epi32(_mm256_add_epi32(s2, c), top_c);
                    s3 = _mm256_sub_epi32(_mm256_add_epi32(s3, d), top_d);
                }
                for (; y < bottom_start; ++y)
                {
                    int32_t row_offset = y * stride + column;
                    narrow_and_store_u8x32(dst + row_offset, s0, s1, s2, s3, divisor);
                    __m256i a, b, c, d;
                    __m256i e, f, g, i;
                    widen_u8x32_to_i32x8(src + row_offset + (radius + 1) * stride, a, b, c, d);
                    widen_u8x32_to_i32x8(src + row_offset - radius * stride, e, f, g, i);
                    s0 = _mm256_sub_epi32(_mm256_add_epi32(s0, a), e);
                    s1 = _mm256_sub_epi32(_mm256_add_epi32(s1, b), f);
                    s2 = _mm256_sub_epi32(_mm256_add_epi32(s2, c), g);
                    s3 = _mm256_sub_epi32(_mm256_add_epi32(s3, d), i);
                }
                for (; y < h; ++y)
                {
                    narrow_and_store_u8x32(dst + static_cast<size_t>(y) * stride + column, s0, s1, s2, s3, divisor);
                    __m256i e, f, g, i;
                    widen_u8x32_to_i32x8(src + static_cast<size_t>(std::max(0, y - radius)) * stride + column, e, f, g, i);
                    s0 = _mm256_sub_epi32(_mm256_add_epi32(s0, bot_a), e);
                    s1 = _mm256_sub_epi32(_mm256_add_epi32(s1, bot_b), f);
                    s2 = _mm256_sub_epi32(_mm256_add_epi32(s2, bot_c), g);
                    s3 = _mm256_sub_epi32(_mm256_add_epi32(s3, bot_d), i);
                }
            }
        }

        ExactByteAverage scalar_divisor(window);
        for (; column < end; ++column)
        {
            int32_t sum = 0;
            for (int32_t dy = -radius; dy <= radius; ++dy)
            {
                sum += src[std::clamp(dy, 0, h - 1) * stride + column];
            }
            for (int32_t y = 0; y < h; ++y)
            {
                dst[y * stride + column] = scalar_divisor.divide(sum);
                sum += src[std::min(h - 1, y + radius + 1) * stride + column]
                     - src[std::max(0, y - radius) * stride + column];
            }
        }

        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_glass_box_blur_horizontal(
    const uint8_t* src,
    uint8_t* dst,
    int32_t w,
    int32_t radius,
    int32_t y0,
    int32_t y1) noexcept
{
    return vnotch::controllers::GlassBoxBlur::Horizontal(src, dst, w, radius, y0, y1);
}

VN_EXPORT int32_t vn_glass_box_blur_vertical(
    const uint8_t* src,
    uint8_t* dst,
    int32_t w,
    int32_t h,
    int32_t radius,
    int32_t x0,
    int32_t x1) noexcept
{
    return vnotch::controllers::GlassBoxBlur::Vertical(src, dst, w, h, radius, x0, x1);
}

} // extern "C"
