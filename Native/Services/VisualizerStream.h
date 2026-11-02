#pragma once
#include <array>
#include <cstdint>

#pragma pack(push, 1)
struct VnComplexFloat { float x, y; };
struct VnVisualizerStreamDspState {
    double agc_peak, prev_kick_energy, prev_snare_energy, kick_accent, snare_accent;
    double prev_rms_for_beat, beat_accent, role_peaks[5], rms_sum_squares;
    int32_t rms_sample_count;
};
#pragma pack(pop)

namespace vnotch::services::visualizer {
using VnSpectrumComplex = VnComplexFloat;
using VnVisualizerDspState = VnVisualizerStreamDspState;

// Owned and used only by the WASAPI capture thread.
struct VisualizerStream {
    std::array<float, 512> samples{};
    std::array<VnComplexFloat, 512> fft{};
    std::array<double, 256> powers{};
    std::array<int32_t, 11> starts{}, ends{};
    std::array<float, 5> targets{}, published{};
    VnVisualizerDspState dsp{};
    int32_t position = 0, rms_count = 0, rms_window = 0, last_bin = 0;
    double rms_sum = 0;
    float rms = 0, beat = 0;
    uint64_t last_publish = 0;
    void Reset(int32_t sample_rate) noexcept;
    bool Process(const uint8_t* data, int32_t frames, int32_t channels,
        int32_t bits, uint8_t is_float, int32_t block_align) noexcept;
};
}
