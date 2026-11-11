#include "../vnotch_native.h"
#include "VisualizerStream.h"

using namespace vnotch::services::visualizer;
VN_EXPORT int32_t __stdcall vn_visualizer_read_sample(const uint8_t*, int32_t, int32_t, uint8_t, int32_t, float*) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_windowed_fft(const float*, int32_t, VnComplexFloat*) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_dsp_reset(VnVisualizerDspState*) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_normalize_amplitude(double, float*) noexcept;
VN_EXPORT int32_t __stdcall vn_visualizer_compute_display_targets(const double*, int32_t, float, VnVisualizerDspState*, float*) noexcept;
VN_EXPORT int32_t vn_visualizer_spectrum_update_ranges(int32_t, int32_t, int32_t, int32_t*, int32_t*, int32_t*) noexcept;
VN_EXPORT int32_t vn_visualizer_spectrum_compute(const VnComplexFloat*, int32_t, const int32_t*, const int32_t*, int32_t, double, double*, double*) noexcept;

void VisualizerStream::Reset(int32_t sample_rate) noexcept {
    *this = {};
    rms_window = std::max(64, sample_rate / 50);
    vn_visualizer_dsp_reset(&dsp);
    vn_visualizer_spectrum_update_ranges(sample_rate, 512, 256, starts.data(), ends.data(), &last_bin);
}

bool VisualizerStream::Process(const uint8_t* data, int32_t frames, int32_t channels,
    int32_t bits, uint8_t is_float, int32_t block_align) noexcept {
    if (frames <= 0 || channels <= 0 || bits <= 0 || block_align < channels * (bits / 8)) return false;
    for (int32_t frame = 0; frame < frames; ++frame) {
        double sum = 0;
        if (data) {
            const uint8_t* bytes = data + static_cast<size_t>(frame) * block_align;
            for (int32_t channel = 0; channel < channels; ++channel) {
                float sample = 0;
                vn_visualizer_read_sample(bytes, block_align, channel * (bits / 8), is_float, bits, &sample);
                sum += sample;
            }
        }
        const float mono = static_cast<float>(sum / channels);
        samples[position++] = mono;
        rms_sum += static_cast<double>(mono) * mono;
        if (++rms_count >= rms_window) {
            vn_visualizer_normalize_amplitude(std::sqrt(rms_sum / rms_count), &rms);
            rms_sum = 0; rms_count = 0;
        }
        if (position == 512) {
            position = 0;
            vn_visualizer_windowed_fft(samples.data(), 512, fft.data());
            std::array<double, 11> energies{};
            vn_visualizer_spectrum_compute(fft.data(), 512, starts.data(), ends.data(), last_bin,
                (2.0 / 512) * (2.0 / 512), powers.data(), energies.data());
            vn_visualizer_compute_display_targets(energies.data(), 11, rms, &dsp, targets.data());
        }
    }
    bool changed = std::abs(beat - static_cast<float>(dsp.beat_accent)) > 0.001f;
    for (size_t i = 0; i < targets.size(); ++i) changed |= std::abs(targets[i] - published[i]) > 0.001f;
    const uint64_t now = GetTickCount64();
    // Keep freshness evidence for sustained tones/silence without transporting PCM.
    if (!changed && now - last_publish < 400) return false;
    published = targets; beat = static_cast<float>(dsp.beat_accent); last_publish = now;
    return true;
}
