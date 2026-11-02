// Compiled with /arch:AVX2. Keep STL and Windows headers out of this file (see vnotch_cpu.h).
#include "../vnotch_simd_kernels.h"
#include <immintrin.h>
#include <cstddef>

namespace vnotch::kernels::avx2
{
    int32_t FftStageBulk(float* flat, const float* stage, int32_t n, int32_t l1) noexcept
    {
        const int32_t l2 = l1 << 1;
        const int32_t vector_end = l1 & ~3;
        for (int32_t block = 0; block < n; block += l2)
        {
            // Four butterflies per iteration: complex multiply with moveldup/movehdup/addsub.
            for (int32_t k = 0; k < vector_end; k += 4)
            {
                float* pa = flat + 2 * static_cast<size_t>(block + k);
                float* pb = pa + 2 * static_cast<size_t>(l1);
                const __m256 a = _mm256_loadu_ps(pa);
                const __m256 b = _mm256_loadu_ps(pb);
                const __m256 w = _mm256_loadu_ps(stage + 2 * static_cast<size_t>(k));
                const __m256 t = _mm256_fmaddsub_ps(
                    b, _mm256_moveldup_ps(w),
                    _mm256_mul_ps(_mm256_permute_ps(b, 0xB1), _mm256_movehdup_ps(w)));
                _mm256_storeu_ps(pa, _mm256_add_ps(a, t));
                _mm256_storeu_ps(pb, _mm256_sub_ps(a, t));
            }
        }
        return vector_end;
    }

    int32_t FftRadix4Bulk(float* flat, const float* first, const float* second, int32_t n, int32_t quarter) noexcept
    {
        const int32_t end = quarter & ~3;
        const auto multiply = [](__m256 value, __m256 w) {
            return _mm256_fmaddsub_ps(value, _mm256_moveldup_ps(w),
                _mm256_mul_ps(_mm256_permute_ps(value, 0xB1), _mm256_movehdup_ps(w)));
        };
        for (int32_t block = 0; block < n; block += 4 * quarter) {
            for (int32_t k = 0; k < end; k += 4) {
                float* a = flat + 2 * (block + k);
                float* b = a + 2 * quarter; float* c = b + 2 * quarter; float* d = c + 2 * quarter;
                const auto w1 = _mm256_loadu_ps(first + 2 * k);
                const auto av = _mm256_loadu_ps(a), cv = _mm256_loadu_ps(c);
                const auto bv = multiply(_mm256_loadu_ps(b), w1), dv = multiply(_mm256_loadu_ps(d), w1);
                const auto ab = _mm256_add_ps(av, bv), amb = _mm256_sub_ps(av, bv);
                const auto cd = multiply(_mm256_add_ps(cv, dv), _mm256_loadu_ps(second + 2 * k));
                const auto cmd = multiply(_mm256_sub_ps(cv, dv), _mm256_loadu_ps(second + 2 * (k + quarter)));
                _mm256_storeu_ps(a, _mm256_add_ps(ab, cd)); _mm256_storeu_ps(c, _mm256_sub_ps(ab, cd));
                _mm256_storeu_ps(b, _mm256_add_ps(amb, cmd)); _mm256_storeu_ps(d, _mm256_sub_ps(amb, cmd));
            }
        }
        return end;
    }
}
