#include "../vnotch_simd_kernels.h"
#include <immintrin.h>

namespace vnotch::kernels::avx2 {
namespace {
__m256 CubeRoot(__m256 value) noexcept {
    // Exponent-derived seed followed by Newton refinement; no per-lane branching.
    const auto positive = _mm256_max_ps(value, _mm256_set1_ps(1e-20f));
    const auto guess = _mm256_add_epi32(_mm256_cvttps_epi32(_mm256_mul_ps(
        _mm256_cvtepi32_ps(_mm256_castps_si256(positive)), _mm256_set1_ps(1.0f / 3))), _mm256_set1_epi32(709921077));
    auto root = _mm256_castsi256_ps(guess);
    for (int i = 0; i < 3; ++i) {
        const auto square = _mm256_mul_ps(root, root);
        auto inverse = _mm256_rcp_ps(square);
        inverse = _mm256_mul_ps(inverse, _mm256_fnmadd_ps(square, inverse, _mm256_set1_ps(2)));
        root = _mm256_mul_ps(_mm256_set1_ps(1.0f / 3),
            _mm256_fmadd_ps(positive, inverse, _mm256_add_ps(root, root)));
    }
    return _mm256_and_ps(root, _mm256_cmp_ps(value, _mm256_setzero_ps(), _CMP_GT_OQ));
}
__m256 Matrix(__m256 r, __m256 g, __m256 b, float x, float y, float z) noexcept {
    return _mm256_fmadd_ps(r, _mm256_set1_ps(x),
        _mm256_fmadd_ps(g, _mm256_set1_ps(y), _mm256_mul_ps(b, _mm256_set1_ps(z))));
}
struct LabVectors { __m256 l, a, b; };
LabVectors Convert(const uint8_t* bgra, const float* linear) noexcept {
    const auto packed = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(bgra));
    const auto mask = _mm256_set1_epi32(255);
    const auto b = _mm256_i32gather_ps(linear, _mm256_and_si256(packed, mask), 4);
    const auto g = _mm256_i32gather_ps(linear, _mm256_and_si256(_mm256_srli_epi32(packed, 8), mask), 4);
    const auto r = _mm256_i32gather_ps(linear, _mm256_and_si256(_mm256_srli_epi32(packed, 16), mask), 4);
    const auto l = CubeRoot(Matrix(r,g,b,0.4122214708f,0.5363325363f,0.0514459929f));
    const auto m = CubeRoot(Matrix(r,g,b,0.2119034982f,0.6806995451f,0.1073969566f));
    const auto s = CubeRoot(Matrix(r,g,b,0.0883024619f,0.2817188376f,0.6299787005f));
    return {Matrix(l,m,s,0.2104542553f,0.7936177850f,-0.0040720468f),
        Matrix(l,m,s,1.9779984951f,-2.4285922050f,0.4505937099f),
        Matrix(l,m,s,0.0259040371f,0.7827717662f,-0.8086757660f)};
}
}
void OklabEight(const uint8_t* bgra, const float* linear, float* out_l, float* out_a, float* out_b) noexcept {
    const auto lab = Convert(bgra, linear);
    _mm256_storeu_ps(out_l, lab.l); _mm256_storeu_ps(out_a, lab.a); _mm256_storeu_ps(out_b, lab.b);
}
void PaletteMetricsEight(const uint8_t* bgra, const float* linear, int32_t x, float center_x, float dy_squared,
    float* out_l, float* out_chroma, float* out_weight, int32_t* out_buckets) noexcept {
    const auto lab = Convert(bgra, linear);
    const auto zero = _mm256_setzero_ps();
    const auto negative_a = _mm256_cmp_ps(lab.a, zero, _CMP_LT_OQ);
    const auto negative_b = _mm256_cmp_ps(lab.b, zero, _CMP_LT_OQ);
    const auto abs_mask = _mm256_castsi256_ps(_mm256_set1_epi32(0x7fffffff));
    const auto a = _mm256_and_ps(lab.a, abs_mask), b = _mm256_and_ps(lab.b, abs_mask);
    // Rotate each quadrant to [0, pi/2], then compare against its eight 10-degree boundaries.
    // This is the same 36-sector partition as atan2, without a transcendental per pixel.
    const auto swap = _mm256_xor_ps(negative_a, negative_b);
    const auto u = _mm256_blendv_ps(a, b, swap), v = _mm256_blendv_ps(b, a, swap);
    auto bucket = _mm256_setzero_si256();
    constexpr float tangents[]{0.1763269807f, 0.3639702343f, 0.5773502692f, 0.8390996312f,
                               1.1917535926f, 1.7320508076f, 2.7474774195f, 5.6712818196f};
    for (float tangent : tangents) {
        const auto crossed = _mm256_cmp_ps(v, _mm256_mul_ps(u, _mm256_set1_ps(tangent)), _CMP_GE_OQ);
        bucket = _mm256_sub_epi32(bucket, _mm256_castps_si256(crossed));
    }
    const auto base = _mm256_blendv_ps(
        _mm256_blendv_ps(zero, _mm256_set1_ps(27), negative_b),
        _mm256_blendv_ps(_mm256_set1_ps(9), _mm256_set1_ps(18), negative_b), negative_a);
    bucket = _mm256_add_epi32(bucket, _mm256_cvttps_epi32(base));
    const auto on_axis = _mm256_and_ps(_mm256_cmp_ps(u, zero, _CMP_EQ_OQ), _mm256_cmp_ps(v, zero, _CMP_GT_OQ));
    bucket = _mm256_sub_epi32(bucket, _mm256_castps_si256(on_axis));
    const auto dx = _mm256_div_ps(_mm256_sub_ps(
        _mm256_add_ps(_mm256_set1_ps(static_cast<float>(x)), _mm256_setr_ps(0,1,2,3,4,5,6,7)),
        _mm256_set1_ps(center_x)), _mm256_set1_ps(center_x));
    const auto distance = _mm256_sqrt_ps(_mm256_add_ps(_mm256_mul_ps(dx, dx), _mm256_set1_ps(dy_squared)));
    const auto weight = _mm256_add_ps(_mm256_set1_ps(1), _mm256_mul_ps(_mm256_set1_ps(.5f),
        _mm256_max_ps(zero, _mm256_sub_ps(_mm256_set1_ps(1), distance))));
    _mm256_storeu_ps(out_l, lab.l);
    _mm256_storeu_ps(out_chroma, _mm256_sqrt_ps(_mm256_add_ps(_mm256_mul_ps(lab.a, lab.a), _mm256_mul_ps(lab.b, lab.b))));
    _mm256_storeu_ps(out_weight, weight);
    _mm256_storeu_si256(reinterpret_cast<__m256i*>(out_buckets), bucket);
}
}
