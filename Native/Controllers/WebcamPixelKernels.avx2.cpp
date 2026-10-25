// Compiled with /arch:AVX2. Keep STL and Windows headers out of this file (see vnotch_cpu.h).
#include "../vnotch_simd_kernels.h"
#include <immintrin.h>
#include <cstddef>

namespace
{
    // Same fixed-point BT.601 math as the scalar WriteYuvBt601, evaluated for 8 pixels held as int32 lanes.
    __forceinline __m256i YuvToBgra8Lanes(__m256i y, __m256i u, __m256i v) noexcept
    {
        const __m256i rounding = _mm256_set1_epi32(128);
        const __m256i luminance = _mm256_mullo_epi32(_mm256_sub_epi32(y, _mm256_set1_epi32(16)), _mm256_set1_epi32(298));
        const __m256i blue_diff = _mm256_sub_epi32(u, _mm256_set1_epi32(128));
        const __m256i red_diff = _mm256_sub_epi32(v, _mm256_set1_epi32(128));

        __m256i b = _mm256_add_epi32(_mm256_add_epi32(luminance, _mm256_mullo_epi32(blue_diff, _mm256_set1_epi32(516))), rounding);
        __m256i g = _mm256_add_epi32(
            _mm256_sub_epi32(
                _mm256_sub_epi32(luminance, _mm256_mullo_epi32(blue_diff, _mm256_set1_epi32(100))),
                _mm256_mullo_epi32(red_diff, _mm256_set1_epi32(208))),
            rounding);
        __m256i r = _mm256_add_epi32(_mm256_add_epi32(luminance, _mm256_mullo_epi32(red_diff, _mm256_set1_epi32(409))), rounding);

        const __m256i zero = _mm256_setzero_si256();
        const __m256i max_value = _mm256_set1_epi32(255);
        b = _mm256_min_epi32(_mm256_max_epi32(_mm256_srai_epi32(b, 8), zero), max_value);
        g = _mm256_min_epi32(_mm256_max_epi32(_mm256_srai_epi32(g, 8), zero), max_value);
        r = _mm256_min_epi32(_mm256_max_epi32(_mm256_srai_epi32(r, 8), zero), max_value);

        return _mm256_or_si256(
            _mm256_or_si256(b, _mm256_slli_epi32(g, 8)),
            _mm256_or_si256(_mm256_slli_epi32(r, 16), _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u))));
    }

    __forceinline __m256i Premultiply16(__m256i channels) noexcept
    {
        // (c * a + 127) / 255 with the exact identity floor(x / 255) == (x + 1 + (x >> 8)) >> 8 for x < 65535.
        const __m256i alpha = _mm256_shufflehi_epi16(_mm256_shufflelo_epi16(channels, 0xFF), 0xFF);
        const __m256i t = _mm256_add_epi16(_mm256_mullo_epi16(channels, alpha), _mm256_set1_epi16(127));
        const __m256i q = _mm256_srli_epi16(
            _mm256_add_epi16(_mm256_add_epi16(t, _mm256_set1_epi16(1)), _mm256_srli_epi16(t, 8)), 8);
        return _mm256_blend_epi16(q, channels, 0x88);
    }
}

namespace vnotch::kernels::avx2
{
    int32_t WebcamRgbInPlaceBulk(uint8_t* pixels, int32_t length, uint8_t is_rgba, int32_t alpha_mode) noexcept
    {
        int32_t i = 0;
        const __m256i swap_rb = _mm256_setr_epi8(
            2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
            2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        const __m256i opaque_alpha = _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u));
        const __m256i zero = _mm256_setzero_si256();
        for (; i + 32 <= length; i += 32)
        {
            __m256i block = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(pixels + i));
            if (is_rgba != 0)
            {
                block = _mm256_shuffle_epi8(block, swap_rb);
            }
            if (alpha_mode == 2)
            {
                block = _mm256_or_si256(block, opaque_alpha);
            }
            else if (alpha_mode == 1)
            {
                const __m256i lo = Premultiply16(_mm256_unpacklo_epi8(block, zero));
                const __m256i hi = Premultiply16(_mm256_unpackhi_epi8(block, zero));
                block = _mm256_packus_epi16(lo, hi);
            }
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(pixels + i), block);
        }
        return i;
    }

    int32_t WebcamGray8Bulk(const uint8_t* src, int32_t pixel_count, uint8_t* dst) noexcept
    {
        int32_t i = 0;
        const __m256i expand = _mm256_setr_epi8(
            0, 0, 0, -1, 1, 1, 1, -1, 2, 2, 2, -1, 3, 3, 3, -1,
            0, 0, 0, -1, 1, 1, 1, -1, 2, 2, 2, -1, 3, 3, 3, -1);
        const __m256i opaque_alpha = _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u));
        for (; i + 8 <= pixel_count; i += 8)
        {
            const __m128i gray = _mm_loadl_epi64(reinterpret_cast<const __m128i*>(src + i));
            const __m256i both_lanes = _mm256_set_m128i(_mm_srli_si128(gray, 4), gray);
            const __m256i bgra = _mm256_or_si256(_mm256_shuffle_epi8(both_lanes, expand), opaque_alpha);
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + static_cast<size_t>(i) * 4u), bgra);
        }
        return i;
    }

    int32_t WebcamNv12RowBulk(const uint8_t* luma_row, const uint8_t* chroma_row, int32_t width, uint8_t* dst_row) noexcept
    {
        const __m128i pick_u = _mm_setr_epi8(0, 0, 2, 2, 4, 4, 6, 6, -1, -1, -1, -1, -1, -1, -1, -1);
        const __m128i pick_v = _mm_setr_epi8(1, 1, 3, 3, 5, 5, 7, 7, -1, -1, -1, -1, -1, -1, -1, -1);
        int32_t x = 0;
        for (; x + 8 <= width; x += 8)
        {
            const __m128i luma = _mm_loadl_epi64(reinterpret_cast<const __m128i*>(luma_row + x));
            const __m128i chroma = _mm_loadl_epi64(reinterpret_cast<const __m128i*>(chroma_row + x));
            const __m256i bgra = YuvToBgra8Lanes(
                _mm256_cvtepu8_epi32(luma),
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(chroma, pick_u)),
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(chroma, pick_v)));
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst_row + static_cast<size_t>(x) * 4u), bgra);
        }
        return x;
    }

    int32_t WebcamYuy2Bulk(const uint8_t* src, int32_t pixel_count, uint8_t* dst) noexcept
    {
        const __m128i pick_y = _mm_setr_epi8(0, 2, 4, 6, 8, 10, 12, 14, -1, -1, -1, -1, -1, -1, -1, -1);
        const __m128i pick_u = _mm_setr_epi8(1, 1, 5, 5, 9, 9, 13, 13, -1, -1, -1, -1, -1, -1, -1, -1);
        const __m128i pick_v = _mm_setr_epi8(3, 3, 7, 7, 11, 11, 15, 15, -1, -1, -1, -1, -1, -1, -1, -1);
        int32_t i = 0;
        for (; i + 8 <= pixel_count; i += 8)
        {
            const __m128i packed = _mm_loadu_si128(reinterpret_cast<const __m128i*>(src + static_cast<size_t>(i) * 2u));
            const __m256i bgra = YuvToBgra8Lanes(
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(packed, pick_y)),
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(packed, pick_u)),
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(packed, pick_v)));
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + static_cast<size_t>(i) * 4u), bgra);
        }
        return i;
    }
}
