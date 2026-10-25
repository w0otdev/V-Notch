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
                const __m256 t = _mm256_addsub_ps(
                    _mm256_mul_ps(b, _mm256_moveldup_ps(w)),
                    _mm256_mul_ps(_mm256_permute_ps(b, 0xB1), _mm256_movehdup_ps(w)));
                _mm256_storeu_ps(pa, _mm256_add_ps(a, t));
                _mm256_storeu_ps(pb, _mm256_sub_ps(a, t));
            }
        }
        return vector_end;
    }
}
