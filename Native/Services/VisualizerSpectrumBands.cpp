#include "vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>

#pragma pack(push, 1)
struct VnComplexFloat
{
    float x;
    float y;
};
#pragma pack(pop)

#pragma fp_contract(off)

namespace vnotch::services
{

class VisualizerSpectrumBands final
{
public:
    static constexpr int32_t kBandCount = 11;
    static constexpr int32_t kBandFrom[kBandCount] = { 20, 60, 250, 500, 2000, 4000, 45, 1400, 350, 6500, 9000 };
    static constexpr int32_t kBandTo[kBandCount] = { 60, 250, 500, 2000, 4000, 8000, 130, 5000, 2400, 11000, 16000 };

    static int32_t UpdateRanges(
        int32_t sample_rate,
        int32_t fft_length,
        int32_t powers_length,
        int32_t* out_starts,
        int32_t* out_ends,
        int32_t* out_last_bin) noexcept
    {
        if (out_starts == nullptr || out_ends == nullptr || out_last_bin == nullptr ||
            sample_rate <= 0 || fft_length < 4 || powers_length <= 1)
        {
            return VN_ERR_INVALID_ARG;
        }

        int32_t last_bin = 0;
        int32_t maximum = powers_length - 1;
        double max_bin = static_cast<double>(maximum);
        double inv_sr = static_cast<double>(fft_length) / static_cast<double>(sample_rate);
        for (int32_t band = 0; band < kBandCount; ++band)
        {
            double start = std::nearbyint(static_cast<double>(kBandFrom[band]) * inv_sr);
            double end = std::nearbyint(static_cast<double>(kBandTo[band]) * inv_sr);
            if (end < start)
            {
                std::swap(start, end);
            }
            int32_t clamped_start = static_cast<int32_t>(std::clamp(start, 1.0, max_bin));
            int32_t clamped_end = static_cast<int32_t>(std::clamp(end, static_cast<double>(clamped_start), max_bin));
            out_starts[band] = clamped_start;
            out_ends[band] = clamped_end;
            last_bin = std::max(last_bin, clamped_end);
        }
        *out_last_bin = last_bin;
        return VN_OK;
    }

    static int32_t Compute(
        const VnComplexFloat* fft,
        int32_t fft_length,
        const int32_t* starts,
        const int32_t* ends,
        int32_t last_bin,
        double power_scale,
        double* powers_scratch,
        double* out_energies) noexcept
    {
        if (fft == nullptr || starts == nullptr || ends == nullptr ||
            powers_scratch == nullptr || out_energies == nullptr ||
            fft_length < 4 || last_bin < 1 || last_bin >= fft_length ||
            !std::isfinite(power_scale) || power_scale < 0.0)
        {
            return VN_ERR_INVALID_ARG;
        }

        for (int32_t band = 0; band < kBandCount; ++band)
        {
            int32_t s = starts[band];
            int32_t e = ends[band];
            if (s < 1 || e < s || e > last_bin)
            {
                return VN_ERR_INVALID_ARG;
            }
        }

        for (int32_t i = 1; i <= last_bin; ++i)
        {
            float x = fft[i].x;
            float y = fft[i].y;
            float mag_sq = (x * x) + (y * y);
            powers_scratch[i] = static_cast<double>(mag_sq) * power_scale;
        }

        for (int32_t band = 0; band < kBandCount; ++band)
        {
            double sum = 0.0;
            int32_t s = starts[band];
            int32_t e = ends[band];
            for (int32_t i = s; i <= e; ++i)
            {
                sum += powers_scratch[i];
            }
            out_energies[band] = std::sqrt(sum / static_cast<double>(e - s + 1));
        }

        return VN_OK;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_visualizer_spectrum_compute(
    const VnComplexFloat* fft,
    int32_t fft_length,
    const int32_t* starts,
    const int32_t* ends,
    int32_t last_bin,
    double power_scale,
    double* powers_scratch,
    double* out_energies) noexcept
{
    return vnotch::services::VisualizerSpectrumBands::Compute(
        fft, fft_length, starts, ends, last_bin, power_scale, powers_scratch, out_energies);
}

VN_EXPORT int32_t vn_visualizer_spectrum_update_ranges(
    int32_t sample_rate,
    int32_t fft_length,
    int32_t powers_length,
    int32_t* out_starts,
    int32_t* out_ends,
    int32_t* out_last_bin) noexcept
{
    return vnotch::services::VisualizerSpectrumBands::UpdateRanges(
        sample_rate, fft_length, powers_length, out_starts, out_ends, out_last_bin);
}

} // extern "C"
