#include "../vnotch_simd_kernels.h"
#include <immintrin.h>

namespace vnotch::kernels::avx2 {
float YoloxBestClass(const float* scores, int32_t* class_id) noexcept {
    auto best = _mm256_setzero_ps();
    auto indices = _mm256_set1_epi32(-1);
    const auto lanes = _mm256_setr_epi32(0,1,2,3,4,5,6,7);
    for (int i = 0; i < 80; i += 8) {
        const auto values = _mm256_loadu_ps(scores + i);
        const auto valid = _mm256_and_ps(_mm256_cmp_ps(values, best, _CMP_GT_OQ),
            _mm256_cmp_ps(values, _mm256_set1_ps(1), _CMP_LE_OQ));
        best = _mm256_blendv_ps(best, values, valid);
        indices = _mm256_blendv_epi8(indices, _mm256_add_epi32(lanes, _mm256_set1_epi32(i)), _mm256_castps_si256(valid));
    }
    alignas(32) float values[8]; alignas(32) int32_t classes[8];
    _mm256_store_ps(values, best); _mm256_store_si256(reinterpret_cast<__m256i*>(classes), indices);
    float result = 0; *class_id = -1;
    for (int i = 0; i < 8; ++i) {
        if (values[i] > result || (values[i] == result && classes[i] >= 0 && (*class_id < 0 || classes[i] < *class_id))) {
            result = values[i]; *class_id = classes[i];
        }
    }
    return result;
}

int32_t SmartCropResizeRow(const uint8_t* top, const uint8_t* bottom,
    const int32_t* x0, const int32_t* x1, const float* weights,
    float vertical_weight, int32_t width, float* b, float* g, float* r) noexcept {
    const auto mask = _mm256_set1_epi32(255);
    const auto wy = _mm256_set1_ps(vertical_weight);
    int32_t x = 0;
    for (; x + 8 <= width; x += 8) {
        const auto left = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(x0 + x));
        const auto right = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(x1 + x));
        const auto tl = _mm256_i32gather_epi32(reinterpret_cast<const int*>(top), left, 4);
        const auto tr = _mm256_i32gather_epi32(reinterpret_cast<const int*>(top), right, 4);
        const auto bl = _mm256_i32gather_epi32(reinterpret_cast<const int*>(bottom), left, 4);
        const auto br = _mm256_i32gather_epi32(reinterpret_cast<const int*>(bottom), right, 4);
        const auto wx = _mm256_loadu_ps(weights + x);
        const auto channel = [&](int shift, float* dst) {
            const auto a = _mm256_cvtepi32_ps(_mm256_and_si256(_mm256_srlv_epi32(tl, _mm256_set1_epi32(shift)), mask));
            const auto c = _mm256_cvtepi32_ps(_mm256_and_si256(_mm256_srlv_epi32(tr, _mm256_set1_epi32(shift)), mask));
            const auto d = _mm256_cvtepi32_ps(_mm256_and_si256(_mm256_srlv_epi32(bl, _mm256_set1_epi32(shift)), mask));
            const auto e = _mm256_cvtepi32_ps(_mm256_and_si256(_mm256_srlv_epi32(br, _mm256_set1_epi32(shift)), mask));
            const auto upper = _mm256_fmadd_ps(_mm256_sub_ps(c, a), wx, a);
            const auto lower = _mm256_fmadd_ps(_mm256_sub_ps(e, d), wx, d);
            _mm256_storeu_ps(dst + x, _mm256_fmadd_ps(_mm256_sub_ps(lower, upper), wy, upper));
        };
        channel(0, b); channel(8, g); channel(16, r);
    }
    return x;
}
}
