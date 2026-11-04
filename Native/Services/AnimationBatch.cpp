#include "vnotch_native.h"
#include "vnotch_cpu.h"

#include "vnotch_simd_kernels.h"

VN_EXPORT void vn_bezier_samples(double p1, double p2, double* values, int intervals) noexcept
{
    if (!values || intervals <= 0) return;
    if (vnotch::cpu::SupportsX64V3()) {
        vnotch::kernels::avx2::BezierSamples(p1, p2, values, intervals);
        return;
    }
    for (int i = 0; i < intervals; ++i) {
        double t = static_cast<double>(i) / intervals, u = 1 - t;
        values[i] = 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t;
    }
    values[intervals] = 1;
}

// Negative values distinguish completed and future words from zero progress.
VN_EXPORT void vn_karaoke_progress(double position, const double* starts,
    const double* ends, double* progress, int count) noexcept
{
    if (!starts || !ends || !progress || count <= 0) return;
    int i = vnotch::cpu::SupportsX64V3() ? vnotch::kernels::avx2::KaraokeProgress(position, starts, ends, progress, count) : 0;
    for (; i < count; ++i) {
        if (position < starts[i]) progress[i] = -1;
        else if (position >= ends[i]) progress[i] = -2;
        else progress[i] = (position - starts[i]) / (ends[i] - starts[i]);
    }
}
