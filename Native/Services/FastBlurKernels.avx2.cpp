// Compiled with /arch:AVX2. Keep STL and Windows headers out of this file (see vnotch_cpu.h).
#include "../vnotch_simd_kernels.h"
#include <immintrin.h>
#include <cstddef>
#include <cstring>

namespace
{
    constexpr uint32_t WindowReciprocal(int32_t window) noexcept
    {
        const uint32_t w = static_cast<uint32_t>(window);
        return ((1u << 24) + w - 1u) / w;
    }

    constexpr int64_t ClampI64(int64_t value, int64_t lo, int64_t hi) noexcept
    {
        return value < lo ? lo : (value > hi ? hi : value);
    }

    __forceinline __m128i LoadPixel(const uint8_t* p) noexcept
    {
        int32_t packed;
        std::memcpy(&packed, p, sizeof(packed));
        return _mm_cvtepu8_epi32(_mm_cvtsi32_si128(packed));
    }

    __forceinline void StorePixel(uint8_t* p, __m128i sum, __m128i reciprocal) noexcept
    {
        const __m128i scaled = _mm_srli_epi32(_mm_mullo_epi32(sum, reciprocal), 24);
        const __m128i packed = _mm_shuffle_epi8(scaled, _mm_setr_epi8(0, 4, 8, 12, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1));
        const int32_t value = _mm_cvtsi128_si32(packed);
        std::memcpy(p, &value, sizeof(value));
    }
}

namespace vnotch::kernels::avx2
{
    int32_t DarkenPixelsBulk(uint8_t* pixels, int32_t length, float factor) noexcept
    {
        // Alpha bytes are restored from the source so the result matches the scalar loop exactly.
        const __m256 scale = _mm256_set1_ps(factor);
        const __m256i alpha_mask = _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u));
        const __m256i fix_order = _mm256_setr_epi32(0, 4, 1, 5, 2, 6, 3, 7);
        int32_t i = 0;
        for (; i + 32 <= length; i += 32)
        {
            const __m256i source = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(pixels + i));
            const __m128i lo = _mm256_castsi256_si128(source);
            const __m128i hi = _mm256_extracti128_si256(source, 1);
            const __m256i s0 = _mm256_cvttps_epi32(_mm256_mul_ps(_mm256_cvtepi32_ps(_mm256_cvtepu8_epi32(lo)), scale));
            const __m256i s1 = _mm256_cvttps_epi32(_mm256_mul_ps(_mm256_cvtepi32_ps(_mm256_cvtepu8_epi32(_mm_srli_si128(lo, 8))), scale));
            const __m256i s2 = _mm256_cvttps_epi32(_mm256_mul_ps(_mm256_cvtepi32_ps(_mm256_cvtepu8_epi32(hi)), scale));
            const __m256i s3 = _mm256_cvttps_epi32(_mm256_mul_ps(_mm256_cvtepi32_ps(_mm256_cvtepu8_epi32(_mm_srli_si128(hi, 8))), scale));
            const __m256i packed = _mm256_permutevar8x32_epi32(
                _mm256_packus_epi16(_mm256_packs_epi32(s0, s1), _mm256_packs_epi32(s2, s3)), fix_order);
            const __m256i merged = _mm256_or_si256(_mm256_andnot_si256(alpha_mask, packed), _mm256_and_si256(alpha_mask, source));
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(pixels + i), merged);
        }
        return i;
    }

    void BoxBlurHorizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
    {
        const int32_t window = 2 * radius + 1;
        const __m128i reciprocal = _mm_set1_epi32(static_cast<int32_t>(WindowReciprocal(window)));
        const __m128i first_edge_count = _mm_set1_epi32(radius + 1);
        const int64_t last_x = static_cast<int64_t>(w) - 1;

        for (int32_t y = 0; y < h; ++y)
        {
            const size_t row = static_cast<size_t>(y) * static_cast<size_t>(w) * 4u;
            const uint8_t* src = source + row;
            uint8_t* dst = target + row;

            __m128i sum = _mm_mullo_epi32(LoadPixel(src), first_edge_count);
            for (int32_t dx = 1; dx <= radius; ++dx)
            {
                const int64_t nx = dx < last_x ? dx : last_x;
                sum = _mm_add_epi32(sum, LoadPixel(src + static_cast<size_t>(nx) * 4u));
            }

            for (int32_t x = 0; x < w; ++x)
            {
                StorePixel(dst + static_cast<size_t>(x) * 4u, sum, reciprocal);

                const int64_t out_x = ClampI64(static_cast<int64_t>(x) - radius, 0, last_x);
                const int64_t in_x = ClampI64(static_cast<int64_t>(x) + 1 + radius, 0, last_x);
                const __m128i delta = _mm_sub_epi32(
                    LoadPixel(src + static_cast<size_t>(in_x) * 4u),
                    LoadPixel(src + static_cast<size_t>(out_x) * 4u));
                sum = _mm_add_epi32(sum, delta);
            }
        }
    }

    void BoxBlurVertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
    {
        const int32_t window = 2 * radius + 1;
        const uint32_t reciprocal = WindowReciprocal(window);
        const size_t row_stride = static_cast<size_t>(w) * 4u;
        const int32_t first_edge_count = radius + 1;

        // The row is processed in fixed-size column strips so the running sums always live on the stack.
        constexpr size_t kStripWidth = 1024;
        int32_t sums[kStripWidth];

        for (size_t strip = 0; strip < row_stride; strip += kStripWidth)
        {
            const size_t remaining = row_stride - strip;
            const size_t count = remaining < kStripWidth ? remaining : kStripWidth;
            const uint8_t* src = source + strip;
            uint8_t* dst = target + strip;

            for (size_t c = 0; c < count; ++c)
            {
                sums[c] = src[c] * first_edge_count;
            }

            for (int32_t dy = 1; dy <= radius; ++dy)
            {
                const uint8_t* row = src + static_cast<size_t>(ClampI64(dy, 0, h - 1)) * row_stride;
                for (size_t c = 0; c < count; ++c)
                {
                    sums[c] += row[c];
                }
            }

            for (int32_t y = 0; y < h; ++y)
            {
                const size_t row = static_cast<size_t>(y) * row_stride;
                const uint8_t* out_row = src + static_cast<size_t>(ClampI64(static_cast<int64_t>(y) - radius, 0, h - 1)) * row_stride;
                const uint8_t* in_row = src + static_cast<size_t>(ClampI64(static_cast<int64_t>(y) + radius + 1, 0, h - 1)) * row_stride;
                uint8_t* dst_row = dst + row;
                for (size_t c = 0; c < count; ++c)
                {
                    dst_row[c] = static_cast<uint8_t>((static_cast<uint32_t>(sums[c]) * reciprocal) >> 24);
                    sums[c] += in_row[c] - out_row[c];
                }
            }
        }
    }
}
