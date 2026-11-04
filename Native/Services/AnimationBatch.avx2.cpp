#include <immintrin.h>
#include "vnotch_simd_kernels.h"

namespace vnotch::kernels::avx2 {

void BezierSamples(double p1, double p2, double* values, int intervals) noexcept
{
    const auto inv = _mm256_set1_pd(1.0 / intervals);
    const auto a = _mm256_set1_pd(1 + 3*p1 - 3*p2);
    const auto b = _mm256_set1_pd(3*p2 - 6*p1);
    const auto c = _mm256_set1_pd(3*p1);
    int i = 0;
    for (; i <= intervals - 4; i += 4) {
        auto t = _mm256_mul_pd(_mm256_setr_pd(i, i+1, i+2, i+3), inv);
        _mm256_storeu_pd(values + i, _mm256_mul_pd(t, _mm256_fmadd_pd(t, _mm256_fmadd_pd(a,t,b), c)));
    }
    for (; i < intervals; ++i) {
        double t = static_cast<double>(i) / intervals, u = 1 - t;
        values[i] = 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t;
    }
    values[intervals] = 1;
}

int KaraokeProgress(double position, const double* starts, const double* ends,
    double* progress, int count) noexcept
{
    const auto now = _mm256_set1_pd(position);
    int i = 0;
    for (; i <= count - 4; i += 4) {
        auto start = _mm256_loadu_pd(starts + i), end = _mm256_loadu_pd(ends + i);
        auto duration = _mm256_max_pd(_mm256_sub_pd(end, start), _mm256_set1_pd(1));
        auto fraction = _mm256_div_pd(_mm256_sub_pd(now, start), duration);
        auto state = _mm256_blendv_pd(fraction, _mm256_set1_pd(-2), _mm256_cmp_pd(now, end, _CMP_GE_OQ));
        state = _mm256_blendv_pd(state, _mm256_set1_pd(-1), _mm256_cmp_pd(now, start, _CMP_LT_OQ));
        _mm256_storeu_pd(progress + i, state);
    }
    return i;
}

void SmoothFourBars(double smoothing, double small_delta, double threshold, const double* current,
    double* smoothed, double* sorted) noexcept
{
    const auto target = _mm256_loadu_pd(current);
    const auto previous = _mm256_loadu_pd(smoothed);
    const auto reference = _mm256_max_pd(previous, target);
    const auto cutoff = _mm256_set1_pd(threshold);
    const auto base = _mm256_set1_pd(smoothing);
    const auto one = _mm256_set1_pd(1);
    const auto smallness = _mm256_sub_pd(one, _mm256_div_pd(reference, cutoff));
    const auto adjusted = _mm256_min_pd(_mm256_set1_pd(0.78),
        _mm256_fmadd_pd(_mm256_set1_pd(small_delta), smallness, base));
    const auto mix = _mm256_blendv_pd(base, adjusted, _mm256_cmp_pd(reference, cutoff, _CMP_LT_OQ));
    const auto result = _mm256_fmadd_pd(previous, mix, _mm256_mul_pd(target, _mm256_sub_pd(one, mix)));
    _mm256_storeu_pd(smoothed, result);
    _mm256_storeu_pd(sorted, result);
}
} // namespace vnotch::kernels::avx2
