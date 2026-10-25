// Compiled with /arch:AVX2. Keep STL and Windows headers out of this file (see vnotch_cpu.h).
#include "../vnotch_simd_kernels.h"
#include <immintrin.h>
#include <cstddef>
#include <cstring>

namespace
{
    constexpr int32_t MinI32(int32_t a, int32_t b) noexcept { return a < b ? a : b; }
    constexpr int32_t MaxI32(int32_t a, int32_t b) noexcept { return a > b ? a : b; }
    constexpr int32_t ClampI32(int32_t v, int32_t lo, int32_t hi) noexcept { return v < lo ? lo : (v > hi ? hi : v); }

    __forceinline void widen_u8x32_to_i32x8(
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

    __forceinline __m256i average_i32x8(__m256i sum, __m256 divisor) noexcept
    {
        return _mm256_cvttps_epi32(_mm256_div_ps(_mm256_cvtepi32_ps(sum), divisor));
    }

    __forceinline __m256i narrow_i32x8_to_i16x16(__m256i lo, __m256i hi) noexcept
    {
        __m256i packed = _mm256_packs_epi32(lo, hi);
        return _mm256_permute4x64_epi64(packed, _MM_SHUFFLE(3, 1, 2, 0));
    }

    __forceinline void narrow_and_store_u8x32(
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

    // One BGRA pixel per 128-bit register: a single add/sub replaces four scalar updates.
    __forceinline __m128i load_px_i32x4(const uint8_t* ptr) noexcept
    {
        int32_t packed;
        std::memcpy(&packed, ptr, sizeof(packed));
        return _mm_cvtepu8_epi32(_mm_cvtsi32_si128(packed));
    }

    __forceinline void store_px_i32x4(uint8_t* ptr, __m128i sum, __m128 divisor) noexcept
    {
        __m128i avg = _mm_cvttps_epi32(_mm_div_ps(_mm_cvtepi32_ps(sum), divisor));
        __m128i words = _mm_packs_epi32(avg, avg);
        int32_t packed = _mm_cvtsi128_si32(_mm_packus_epi16(words, words));
        std::memcpy(ptr, &packed, sizeof(packed));
    }
}

namespace vnotch::kernels::avx2
{
    void GlassBoxBlurHorizontal(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t radius,
        int32_t y0,
        int32_t y1) noexcept
    {
        const size_t row_bytes = static_cast<size_t>(w) * 4u;
        const auto src_px = [src, row_bytes](int32_t y, int32_t x) noexcept
        {
            return src + static_cast<size_t>(y) * row_bytes + static_cast<size_t>(x) * 4u;
        };
        const auto dst_px = [dst, row_bytes](int32_t y, int32_t x) noexcept
        {
            return dst + static_cast<size_t>(y) * row_bytes + static_cast<size_t>(x) * 4u;
        };

        const __m128 divisor = _mm_set1_ps(static_cast<float>(2 * radius + 1));
        const int32_t left_end = MinI32(w, radius);
        const int32_t right_start = MaxI32(left_end, w - radius - 1);

        for (int32_t y = y0; y < y1; ++y)
        {
            __m128i sum = _mm_setzero_si128();
            for (int32_t dx = -radius; dx <= radius; ++dx)
            {
                sum = _mm_add_epi32(sum, load_px_i32x4(src_px(y, ClampI32(dx, 0, w - 1))));
            }

            const __m128i first = load_px_i32x4(src_px(y, 0));
            int32_t x = 0;
            for (; x < left_end; ++x)
            {
                store_px_i32x4(dst_px(y, x), sum, divisor);
                sum = _mm_sub_epi32(
                    _mm_add_epi32(sum, load_px_i32x4(src_px(y, MinI32(w - 1, x + 1 + radius)))),
                    first);
            }
            for (; x < right_start; ++x)
            {
                store_px_i32x4(dst_px(y, x), sum, divisor);
                sum = _mm_sub_epi32(
                    _mm_add_epi32(sum, load_px_i32x4(src_px(y, x + radius + 1))),
                    load_px_i32x4(src_px(y, x - radius)));
            }

            const __m128i last = load_px_i32x4(src_px(y, w - 1));
            for (; x < w; ++x)
            {
                store_px_i32x4(dst_px(y, x), sum, divisor);
                sum = _mm_sub_epi32(
                    _mm_add_epi32(sum, last),
                    load_px_i32x4(src_px(y, MaxI32(0, x - radius))));
            }
        }
    }

    int32_t GlassBoxBlurVerticalBulk(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t h,
        int32_t radius,
        int32_t column_start,
        int32_t column_end) noexcept
    {
        const ptrdiff_t stride = static_cast<ptrdiff_t>(w) * 4;
        const int32_t top_end = MinI32(h, radius);
        const int32_t bottom_start = MaxI32(top_end, h - radius - 1);
        const ptrdiff_t last_row_offset = static_cast<ptrdiff_t>(h - 1) * stride;
        const __m256 divisor = _mm256_set1_ps(static_cast<float>(2 * radius + 1));

        int32_t column = column_start;
        for (; column <= column_end - 32; column += 32)
        {
            __m256i s0 = _mm256_setzero_si256();
            __m256i s1 = s0;
            __m256i s2 = s0;
            __m256i s3 = s0;

            for (int32_t dy = -radius; dy <= radius; ++dy)
            {
                __m256i a, b, c, d;
                widen_u8x32_to_i32x8(src + static_cast<ptrdiff_t>(ClampI32(dy, 0, h - 1)) * stride + column, a, b, c, d);
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
                narrow_and_store_u8x32(dst + static_cast<ptrdiff_t>(y) * stride + column, s0, s1, s2, s3, divisor);
                __m256i a, b, c, d;
                widen_u8x32_to_i32x8(src + static_cast<ptrdiff_t>(MinI32(h - 1, y + radius + 1)) * stride + column, a, b, c, d);
                s0 = _mm256_sub_epi32(_mm256_add_epi32(s0, a), top_a);
                s1 = _mm256_sub_epi32(_mm256_add_epi32(s1, b), top_b);
                s2 = _mm256_sub_epi32(_mm256_add_epi32(s2, c), top_c);
                s3 = _mm256_sub_epi32(_mm256_add_epi32(s3, d), top_d);
            }
            for (; y < bottom_start; ++y)
            {
                const ptrdiff_t row_offset = static_cast<ptrdiff_t>(y) * stride + column;
                narrow_and_store_u8x32(dst + row_offset, s0, s1, s2, s3, divisor);
                __m256i a, b, c, d;
                __m256i e, f, g, i;
                widen_u8x32_to_i32x8(src + row_offset + static_cast<ptrdiff_t>(radius + 1) * stride, a, b, c, d);
                widen_u8x32_to_i32x8(src + row_offset - static_cast<ptrdiff_t>(radius) * stride, e, f, g, i);
                s0 = _mm256_sub_epi32(_mm256_add_epi32(s0, a), e);
                s1 = _mm256_sub_epi32(_mm256_add_epi32(s1, b), f);
                s2 = _mm256_sub_epi32(_mm256_add_epi32(s2, c), g);
                s3 = _mm256_sub_epi32(_mm256_add_epi32(s3, d), i);
            }
            for (; y < h; ++y)
            {
                narrow_and_store_u8x32(dst + static_cast<ptrdiff_t>(y) * stride + column, s0, s1, s2, s3, divisor);
                __m256i e, f, g, i;
                widen_u8x32_to_i32x8(src + static_cast<ptrdiff_t>(MaxI32(0, y - radius)) * stride + column, e, f, g, i);
                s0 = _mm256_sub_epi32(_mm256_add_epi32(s0, bot_a), e);
                s1 = _mm256_sub_epi32(_mm256_add_epi32(s1, bot_b), f);
                s2 = _mm256_sub_epi32(_mm256_add_epi32(s2, bot_c), g);
                s3 = _mm256_sub_epi32(_mm256_add_epi32(s3, bot_d), i);
            }
        }
        return column;
    }
}
