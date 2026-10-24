#include "../vnotch_common.h"
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <immintrin.h>
#include <numbers>

// Music visualizer DSP: PCM/float sample decoding, Hamming-windowed forward FFT, AGC/beat/spectral shaping,
// and bar animation physics.

namespace vnotch::services::visualizer {

constexpr int32_t kMaxFftLength = 4096;
constexpr int32_t kBarCount = 5;
constexpr int32_t kBandCount = 11;

constexpr double kMinHeightRatio = 0.08;
constexpr double kMaxHeightRatio = 1.00;
constexpr double kAlphaAttack = 0.70;
constexpr double kAlphaRelease = 0.84;
constexpr double kAlphaPauseRelease = 0.985;
constexpr double kTauOpacity = 200.0;
constexpr double kNoAudioPulseAmplitude = 0.05;
constexpr double kNoAudioPulseBase = 0.05;
constexpr double kLegacyRhythmMinMix = 0.10;
constexpr double kLegacyRhythmMaxMix = 0.25;
constexpr double kAudioPresenceThreshold = 0.0012;
constexpr double kDownwardDropBoost = 0.18;
constexpr double kMinReleaseAlpha = 0.66;
constexpr double kMotionContrast = 1.22;
constexpr double kRightBiasStrength = 0.12;
constexpr double kRightBiasDeadzone = 0.025;
constexpr double kMinHeightChangeThreshold = 0.0012;
constexpr double kSmallBarHeightThreshold = 0.30;
constexpr double kSmallBarTargetDeadzone = 0.001;
constexpr double kAudioReactiveMotionFloor = 0.04;
constexpr double kAudioReactiveRhythmPush = 0.08;
constexpr double kAudioReactiveCrossBandLift = 0.08;
constexpr double kReferenceFrameMs = 16.0;

constexpr double kMinDb = -90.0;
constexpr double kMaxDb = 0.0;
constexpr double kCompressionPower = 0.38;
constexpr double kSpectralPreGain = 48.0;
constexpr double kAgcFloor = 0.008;
constexpr double kAgcRelease = 0.985;
constexpr double kKickTransientThreshold = 0.018;
constexpr double kSnareTransientThreshold = 0.016;
constexpr double kKickTransientGain = 9.0;
constexpr double kSnareTransientGain = 10.0;
constexpr double kKickAccentDecay = 0.955;
constexpr double kSnareAccentDecay = 0.950;
constexpr double kBeatTransientThreshold = 0.014;
constexpr double kBeatTransientGain = 10.0;
constexpr double kBeatAccentDecay = 0.955;
constexpr double kBeatRmsDeltaThreshold = 0.004;
constexpr double kBassDominanceStart = 1.10;
constexpr double kBassDominanceSpan = 1.00;
constexpr double kMaxLowAttenuation = 0.34;
constexpr double kMaxKickAttenuation = 0.42;
constexpr double kSpectralContrastStart = 0.03;
constexpr double kSpectralContrastSpan = 0.30;
constexpr double kSpectralContrastStrength = 1.60;
constexpr double kSpectralDominantBoost = 0.30;
constexpr double kSpectralSubtractiveCut = 0.28;
constexpr double kDynamicRangeExpansionPower = 1.20;
constexpr double kDynamicRangeExpansionBlend = 0.18;
constexpr double kBarContrastStart = 0.06;
constexpr double kBarContrastSpan = 0.35;
constexpr double kBarContrastStrength = 0.85;
constexpr double kBarContrastBoost = 0.22;
constexpr double kBarContrastCut = 0.16;
constexpr double kVisualMaxPeakTarget = 0.74;
constexpr double kVisualMaxFloor = 0.18;
constexpr double kVisualMaxBandFloor = 0.06;
constexpr double kVisualMaxBandLift = 0.10;
constexpr double kVisualMaxGainLimit = 1.55;
constexpr double kRolePeakRelease = 0.992;
constexpr double kRolePeakFloor = 0.16;

constexpr std::array<double, kBarCount> kRoleTargets = { 0.72, 0.66, 0.62, 0.56, 0.50 };
constexpr std::array<double, kBarCount> kMaxGains = { 1.45, 1.55, 1.35, 1.85, 2.05 };
constexpr std::array<double, kBarCount> kFallback = { 0.025, 0.035, 0.070, 0.030, 0.022 };
constexpr std::array<double, kBarCount> kCaps = { 0.86, 0.82, 0.76, 0.70, 0.64 };

#pragma pack(push, 1)
struct VnSpectrumComplex {
    float x;
    float y;
};

struct VnVisualizerDspState {
    double agc_peak;
    double prev_kick_energy;
    double prev_snare_energy;
    double kick_accent;
    double snare_accent;
    double prev_rms_for_beat;
    double beat_accent;
    double role_peaks[kBarCount];
    double rms_sum_squares;
    int32_t rms_sample_count;
};
#pragma pack(pop)

bool IsPowerOfTwo(int32_t value) noexcept {
    return value >= 4 && value <= kMaxFftLength && (value & (value - 1)) == 0;
}

const double* HammingWindow(int32_t length) noexcept {
    static std::array<std::array<double, kMaxFftLength>, 11> tables{};
    static std::array<std::atomic<bool>, 11> ready{};
    static SRWLOCK lock = SRWLOCK_INIT;
    const int slot = std::countr_zero(static_cast<uint32_t>(length)) - 2;
    if (slot < 0 || slot >= static_cast<int>(tables.size())) return nullptr;
    // Fast path is lock-free; the lock only serialises the one-time table fill.
    if (!ready[static_cast<size_t>(slot)].load(std::memory_order_acquire)) {
        AcquireSRWLockExclusive(&lock);
        if (!ready[static_cast<size_t>(slot)].load(std::memory_order_relaxed)) {
            auto& table = tables[static_cast<size_t>(slot)];
            for (int32_t i = 0; i < length; ++i)
                table[static_cast<size_t>(i)] = 0.54 - 0.46 * std::cos((2.0 * std::numbers::pi * i) / (length - 1));
            ready[static_cast<size_t>(slot)].store(true, std::memory_order_release);
        }
        ReleaseSRWLockExclusive(&lock);
    }
    return tables[static_cast<size_t>(slot)].data();
}

// Per-stage twiddle factors exp(-i*pi*j/l1) for every butterfly half-size l1 = 1, 2, 4, ... kMaxFftLength / 2.
// Stage l1 occupies entries [l1 - 1, 2 * l1 - 1), each stored as an interleaved (re, im) float pair, so the
// butterfly loop reads them contiguously and they are computed in double instead of by float recurrence.
const float* FftTwiddles() noexcept {
    alignas(32) static float table[2 * kMaxFftLength];
    static std::atomic<bool> ready{false};
    static SRWLOCK lock = SRWLOCK_INIT;
    if (!ready.load(std::memory_order_acquire)) {
        AcquireSRWLockExclusive(&lock);
        if (!ready.load(std::memory_order_relaxed)) {
            for (int32_t l1 = 1; l1 < kMaxFftLength; l1 <<= 1) {
                for (int32_t j = 0; j < l1; ++j) {
                    const double angle = -std::numbers::pi * static_cast<double>(j) / static_cast<double>(l1);
                    const size_t index = 2u * static_cast<size_t>(l1 - 1 + j);
                    table[index] = static_cast<float>(std::cos(angle));
                    table[index + 1] = static_cast<float>(std::sin(angle));
                }
            }
            ready.store(true, std::memory_order_release);
        }
        ReleaseSRWLockExclusive(&lock);
    }
    return table;
}

void FftForward(VnSpectrumComplex* data, int32_t m) noexcept {
    const int32_t n = 1 << m;
    int32_t i2 = n >> 1;
    int32_t j = 0;
    for (int32_t i = 0; i < n - 1; ++i) {
        if (i < j) std::swap(data[i], data[j]);
        int32_t k = i2;
        while (k <= j) {
            j -= k;
            k >>= 1;
        }
        j += k;
    }

    const float* twiddles = FftTwiddles();
    float* const flat = reinterpret_cast<float*>(data);
    for (int32_t l = 0; l < m; ++l) {
        const int32_t l1 = 1 << l;
        const int32_t l2 = l1 << 1;
        const float* stage = twiddles + 2 * static_cast<size_t>(l1 - 1);
        for (int32_t block = 0; block < n; block += l2) {
            int32_t k = 0;
            // Four butterflies per iteration: complex multiply with moveldup/movehdup/addsub.
            for (; k + 4 <= l1; k += 4) {
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
            for (; k < l1; ++k) {
                VnSpectrumComplex& lo = data[block + k];
                VnSpectrumComplex& hi = data[block + k + l1];
                const float wr = stage[2 * static_cast<size_t>(k)];
                const float wi = stage[2 * static_cast<size_t>(k) + 1];
                const float t1 = wr * hi.x - wi * hi.y;
                const float t2 = wr * hi.y + wi * hi.x;
                hi.x = lo.x - t1;
                hi.y = lo.y - t2;
                lo.x += t1;
                lo.y += t2;
            }
        }
    }

    // n is a power of two, so multiplying by 1/n is bit-identical to dividing by n.
    const float inv_scale = 1.0f / static_cast<float>(n);
    for (int32_t i = 0; i < n; ++i) {
        data[i].x *= inv_scale;
        data[i].y *= inv_scale;
    }
}

double NormalizeAmplitude(double amplitude) noexcept {
    const double db = 20.0 * std::log10(std::max(amplitude, 1e-9));
    const double normalized = std::clamp((db - kMinDb) / (kMaxDb - kMinDb), 0.0, 1.0);
    return std::pow(normalized, kCompressionPower);
}

double ExpandDynamicRange(double value) noexcept {
    const double clamped = std::clamp(value, 0.0, 1.0);
    if (clamped <= 0.0) return 0.0;
    const double expanded = std::pow(clamped, kDynamicRangeExpansionPower);
    return (expanded * (1.0 - kDynamicRangeExpansionBlend)) + (clamped * kDynamicRangeExpansionBlend);
}

double EnhanceBand(double band, double avg, double contrast) noexcept {
    const double delta = band - avg;
    double enhanced = band + (delta * (kSpectralContrastStrength * contrast));
    if (delta >= 0.0) {
        enhanced *= 1.0 + (kSpectralDominantBoost * contrast);
    } else {
        enhanced *= 1.0 - (kSpectralSubtractiveCut * contrast);
    }
    return std::clamp(enhanced, 0.0, 1.0);
}

void ApplySpectralContrast(
    double& sub_bass, double& bass, double& low_mid, double& mid,
    double& high_mid, double& high, double& kick, double& snare, double& rms) noexcept {
    const double max_v = std::max({ sub_bass, bass, low_mid, mid, high_mid, high });
    const double min_v = std::min({ sub_bass, bass, low_mid, mid, high_mid, high });
    const double spread = std::clamp(max_v - min_v, 0.0, 1.0);
    const double contrast = std::clamp((spread - kSpectralContrastStart) / kSpectralContrastSpan, 0.0, 1.0);

    if (contrast <= 0.0001) {
        sub_bass = ExpandDynamicRange(sub_bass);
        bass = ExpandDynamicRange(bass);
        low_mid = ExpandDynamicRange(low_mid);
        mid = ExpandDynamicRange(mid);
        high_mid = ExpandDynamicRange(high_mid);
        high = ExpandDynamicRange(high);
        kick = ExpandDynamicRange(kick);
        snare = ExpandDynamicRange(snare);
        rms = ExpandDynamicRange(rms);
        return;
    }

    const double avg = (sub_bass + bass + low_mid + mid + high_mid + high) / 6.0;

    sub_bass = EnhanceBand(sub_bass, avg, contrast);
    bass = EnhanceBand(bass, avg, contrast);
    low_mid = EnhanceBand(low_mid, avg, contrast);
    mid = EnhanceBand(mid, avg, contrast);
    high_mid = EnhanceBand(high_mid, avg, contrast);
    high = EnhanceBand(high, avg, contrast);

    const double bass_delta = (sub_bass + bass) / 2.0 - avg;
    const double high_delta = (high_mid + high) / 2.0 - avg;

    kick = std::clamp(kick * (1.0 + (bass_delta * 0.70 * contrast)), 0.0, 1.0);
    snare = std::clamp(snare * (1.0 + (high_delta * 0.70 * contrast)), 0.0, 1.0);

    sub_bass = ExpandDynamicRange(sub_bass);
    bass = ExpandDynamicRange(bass);
    low_mid = ExpandDynamicRange(low_mid);
    mid = ExpandDynamicRange(mid);
    high_mid = ExpandDynamicRange(high_mid);
    high = ExpandDynamicRange(high);
    kick = ExpandDynamicRange(kick);
    snare = ExpandDynamicRange(snare);
    rms = ExpandDynamicRange(rms);
}

double CompressRoleUpperRange(double value, double cap) noexcept {
    const double clamped = std::clamp(value, 0.0, 1.0);
    const double knee = cap * 0.72;
    if (clamped <= knee) return clamped;

    const double over = (clamped - knee) / std::max(0.001, 1.0 - knee);
    const double compressed = knee + ((cap - knee) * (1.0 - std::exp(-over * 1.65)));
    return std::min(cap, compressed);
}

void ApplyInstrumentRoleResponse(float* targets, double* role_peaks, double rms) noexcept {
    const double energy = std::clamp(rms, 0.0, 1.0);
    for (int32_t i = 0; i < kBarCount; ++i) {
        const size_t idx = static_cast<size_t>(i);
        const double raw = std::clamp(static_cast<double>(targets[idx]), 0.0, 1.0);
        role_peaks[idx] = std::max(raw, role_peaks[idx] * kRolePeakRelease);

        const double gain = std::clamp(kRoleTargets[idx] / std::max(kRolePeakFloor, role_peaks[idx]), 0.70, kMaxGains[idx]);
        double adapted = raw * gain;
        adapted += kFallback[idx] * energy * (1.0 - std::clamp(raw * 2.0, 0.0, 1.0));
        targets[idx] = static_cast<float>(std::clamp(CompressRoleUpperRange(adapted, kCaps[idx]), 0.0, kCaps[idx]));
    }
}

void ApplyBarContrast(float* targets) noexcept {
    double max_v = 0.0;
    double min_v = 1.0;
    double sum = 0.0;
    for (int32_t i = 0; i < kBarCount; ++i) {
        const double v = std::clamp(static_cast<double>(targets[i]), 0.0, 1.0);
        if (v > max_v) max_v = v;
        if (v < min_v) min_v = v;
        sum += v;
    }

    const double spread = std::clamp(max_v - min_v, 0.0, 1.0);
    const double contrast = std::clamp((spread - kBarContrastStart) / kBarContrastSpan, 0.0, 1.0);
    if (contrast <= 0.0001) return;

    const double avg = sum / kBarCount;
    for (int32_t i = 0; i < kBarCount; ++i) {
        const double v = std::clamp(static_cast<double>(targets[i]), 0.0, 1.0);
        const double delta = v - avg;
        double enhanced = v + (delta * (kBarContrastStrength * contrast));
        if (delta >= 0.0) {
            enhanced *= 1.0 + (kBarContrastBoost * contrast);
        } else {
            enhanced *= 1.0 - (kBarContrastCut * contrast);
        }
        targets[i] = static_cast<float>(std::clamp(enhanced, 0.0, 1.0));
    }
}

double CompressUpperRange(double value) noexcept {
    const double clamped = std::clamp(value, 0.0, 1.0);
    constexpr double knee = 0.62;
    if (clamped <= knee) return clamped;

    const double over = (clamped - knee) / (1.0 - knee);
    const double compressed = knee + ((1.0 - knee) * (1.0 - std::exp(-over * 1.35)) / (1.0 - std::exp(-1.35)));
    return std::min(0.86, compressed);
}

void NormalizeDisplayTargetsToMaxVisual(float* targets) noexcept {
    double peak = 0.0;
    double sum = 0.0;
    for (int32_t i = 0; i < kBarCount; ++i) {
        const double value = std::clamp(static_cast<double>(targets[i]), 0.0, 1.0);
        peak = std::max(peak, value);
        sum += value;
    }
    if (peak <= 0.0001) return;

    const double desired_gain = kVisualMaxPeakTarget / std::max(kVisualMaxFloor, peak);
    const double gain = std::clamp(desired_gain, 0.70, kVisualMaxGainLimit);
    const double energy = std::clamp(sum / kBarCount * gain, 0.0, 1.0);
    const double band_floor = kVisualMaxBandFloor * energy;

    for (int32_t i = 0; i < kBarCount; ++i) {
        const size_t idx = static_cast<size_t>(i);
        const double boosted = CompressUpperRange(std::clamp(static_cast<double>(targets[idx]) * gain, 0.0, 1.0));
        const double lifted = boosted < band_floor
            ? boosted + ((band_floor - boosted) * kVisualMaxBandLift)
            : boosted;
        targets[idx] = static_cast<float>(std::clamp(lifted, 0.0, kCaps[idx]));
    }
}

uint32_t ContinueHashInt(uint32_t hash, int32_t value) noexcept {
    const uint32_t u = static_cast<uint32_t>(value);
    hash = (hash ^ (u & 0xFFu)) * 16777619u;
    hash = (hash ^ ((u >> 8) & 0xFFu)) * 16777619u;
    hash = (hash ^ ((u >> 16) & 0xFFu)) * 16777619u;
    hash = (hash ^ (u >> 24)) * 16777619u;
    hash ^= hash >> 16;
    hash *= 0x85EBCA6Bu;
    hash ^= hash >> 13;
    hash *= 0xC2B2AE35u;
    hash ^= hash >> 16;
    return hash;
}

double MapNormalizedToHeight(double normalized) noexcept {
    const double clamped = std::clamp(normalized, 0.0, 1.0);
    return kMinHeightRatio + clamped * (kMaxHeightRatio - kMinHeightRatio);
}

double ApplyMotionContrast(double normalized) noexcept {
    const double clamped = std::clamp(normalized, 0.0, 1.0);
    return std::clamp(0.5 + ((clamped - 0.5) * kMotionContrast), 0.0, 1.0);
}

double ApplyBarPersonality(int32_t bar_index, double normalized, double energy) noexcept {
    const double clamped = std::clamp(normalized, 0.0, 1.0);
    constexpr std::array<double, kBarCount> kExponents = { 1.42, 1.18, 0.88, 1.36, 1.58 };
    constexpr std::array<double, kBarCount> kGains = { 1.06, 1.00, 1.10, 0.92, 0.78 };
    const double exponent = (bar_index >= 0 && bar_index < kBarCount) ? kExponents[static_cast<size_t>(bar_index)] : 1.0;
    const double gain = (bar_index >= 0 && bar_index < kBarCount) ? kGains[static_cast<size_t>(bar_index)] : 1.0;

    const double shaped = std::pow(clamped, exponent) * gain;
    const double energy_lift = energy * (bar_index == 2 ? 0.045 : 0.012);
    return std::clamp(shaped + energy_lift, 0.0, 1.0);
}

double GetNoAudioPulseAt(uint32_t hash, double t) noexcept {
    const double phase = (hash % 1000u) / 1000.0 * std::numbers::pi * 2.0;
    const double freq = 0.13 + (hash % 15u) / 200.0;
    const double wave_primary = 0.5 + 0.5 * std::sin((t * freq * std::numbers::pi * 2.0) + phase);
    const double wave_secondary = 0.5 + 0.5 * std::sin((t * (freq * 1.35) * std::numbers::pi * 2.0) + (phase * 0.37));
    const double wave = (wave_primary * 0.85) + (wave_secondary * 0.15);
    return kNoAudioPulseBase + (wave * kNoAudioPulseAmplitude);
}

double GetBeatLiftWeight(int32_t bar_index) noexcept {
    constexpr std::array<double, kBarCount> kWeights = { 0.34, 0.26, 0.10, 0.18, 0.14 };
    return (bar_index >= 0 && bar_index < kBarCount) ? kWeights[static_cast<size_t>(bar_index)] : 0.18;
}

double GetAudioReactiveFloor(int32_t index, uint32_t hash, double t, double energy, double beat_accent) noexcept {
    const double phase = (hash % 1000u) / 1000.0 * std::numbers::pi * 2.0;
    const double freq = 0.34 + ((hash % 21u) / 200.0) + (energy * 0.12);
    const double pulse = 0.5 + 0.5 * std::sin((t * freq * std::numbers::pi * 2.0) + phase);
    const double counter_pulse = 0.5 + 0.5 * std::sin((t * (freq * 1.73) * std::numbers::pi * 2.0) + (phase * 0.43));
    const double motion = (pulse * 0.72) + (counter_pulse * 0.28);
    constexpr std::array<double, kBarCount> kFloorScales = { 0.55, 0.78, 1.02, 0.66, 0.46 };
    const double floor_scale = (index >= 0 && index < kBarCount) ? kFloorScales[static_cast<size_t>(index)] : 1.0;
    double floor_val = (kAudioReactiveMotionFloor + (energy * 0.07) + (motion * (0.035 + (energy * 0.04)))) * floor_scale;
    floor_val += beat_accent * GetBeatLiftWeight(index) * 0.10;
    return std::clamp(floor_val, 0.0, 0.26);
}

double GetAudioReactiveRhythmAt(uint32_t hash, double t, double energy) noexcept {
    const double phase = (hash % 1000u) / 1000.0 * std::numbers::pi * 2.0;
    const double base_freq = 0.20 + (hash % 18u) / 200.0;
    const double speed = 0.28 + (energy * 0.16);

    double value = std::sin((t * base_freq * speed * std::numbers::pi * 2.0) + phase) * 0.38;
    value += std::sin((t * (0.13 + (energy * 0.10)) * std::numbers::pi * 2.0) + (phase * 0.5)) * 0.18;
    value += std::sin((t * (base_freq * 0.32) * std::numbers::pi * 2.0) + (phase * 1.7)) * 0.06;

    const double noise_rate = 0.45 + (energy * 0.6);
    const uint32_t noise_seed = ContinueHashInt(hash, static_cast<int32_t>(std::floor(t * noise_rate)));
    value += (((noise_seed % 200u) / 100.0) - 1.0) * (0.003 + (energy * 0.005));

    return std::clamp(0.5 + value, 0.0, 1.0);
}

} // namespace vnotch::services::visualizer

namespace vz = vnotch::services::visualizer;

extern "C" {

VN_EXPORT int32_t __stdcall vn_visualizer_read_sample(
    const uint8_t* buffer, int32_t length, int32_t offset, uint8_t is_float, int32_t bits_per_sample, float* out_sample) noexcept {
    if (!buffer || !out_sample || length < 0 || offset < 0) return VN_ERR_INVALID_ARG;
    const int32_t width = bits_per_sample / 8;
    if (width <= 0 || offset > length - width) return VN_ERR_INVALID_ARG;
    const uint8_t* p = buffer + offset;
    if (is_float != 0) {
        if (bits_per_sample == 64) {
            double value;
            std::memcpy(&value, p, sizeof(value));
            *out_sample = static_cast<float>(value);
        } else {
            std::memcpy(out_sample, p, sizeof(float));
        }
        return VN_OK;
    }
    switch (bits_per_sample) {
        case 8:
            *out_sample = (static_cast<int32_t>(p[0]) - 128) / 128.0f;
            break;
        case 16: {
            int16_t value;
            std::memcpy(&value, p, sizeof(value));
            *out_sample = value / 32768.0f;
            break;
        }
        case 24: {
            int32_t value = p[0] | (p[1] << 8) | (p[2] << 16);
            if (value & 0x800000) value |= static_cast<int32_t>(0xFF000000u);
            *out_sample = value / 8388608.0f;
            break;
        }
        case 32: {
            int32_t value;
            std::memcpy(&value, p, sizeof(value));
            *out_sample = value / 2147483648.0f;
            break;
        }
        default:
            *out_sample = 0.0f;
            break;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_windowed_fft(const float* samples, int32_t length, vz::VnSpectrumComplex* out_fft) noexcept {
    if (!samples || !out_fft || !vz::IsPowerOfTwo(length)) return VN_ERR_INVALID_ARG;
    const double* window = vz::HammingWindow(length);
    if (!window) return VN_ERR_INVALID_ARG;
    for (int32_t i = 0; i < length; ++i) {
        out_fft[i].x = static_cast<float>(samples[i] * window[i]);
        out_fft[i].y = 0.0f;
    }
    vz::FftForward(out_fft, std::countr_zero(static_cast<uint32_t>(length)));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_dsp_reset(vz::VnVisualizerDspState* out_state) noexcept {
    if (!out_state) return VN_ERR_INVALID_ARG;
    std::memset(out_state, 0, sizeof(*out_state));
    out_state->agc_peak = 0.35;
    out_state->role_peaks[0] = 0.38;
    out_state->role_peaks[1] = 0.36;
    out_state->role_peaks[2] = 0.40;
    out_state->role_peaks[3] = 0.34;
    out_state->role_peaks[4] = 0.32;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_normalize_amplitude(double amplitude, float* out_normalized) noexcept {
    if (!out_normalized) return VN_ERR_INVALID_ARG;
    *out_normalized = static_cast<float>(vz::NormalizeAmplitude(amplitude));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_compute_display_targets(
    const double* energies,
    int32_t energy_count,
    float rms_normalized,
    vz::VnVisualizerDspState* io_state,
    float* out_targets) noexcept {
    if (!energies || energy_count < vz::kBandCount || !io_state || !out_targets) return VN_ERR_INVALID_ARG;

    double sub_bass = vz::NormalizeAmplitude(energies[0] * (vz::kSpectralPreGain * 0.9));
    double bass = vz::NormalizeAmplitude(energies[1] * (vz::kSpectralPreGain * 0.85));
    double low_mid = vz::NormalizeAmplitude(energies[2] * (vz::kSpectralPreGain * 1.2));
    double mid = vz::NormalizeAmplitude(energies[3] * (vz::kSpectralPreGain * 1.5));
    double high_mid = vz::NormalizeAmplitude(energies[4] * (vz::kSpectralPreGain * 3.2));
    double high = vz::NormalizeAmplitude(energies[5] * (vz::kSpectralPreGain * 3.8));
    double kick = vz::NormalizeAmplitude(energies[6] * (vz::kSpectralPreGain * 1.0));
    double snare = vz::NormalizeAmplitude(energies[7] * (vz::kSpectralPreGain * 2.8));
    double melody = vz::NormalizeAmplitude(energies[8] * (vz::kSpectralPreGain * 1.7));
    double hat = vz::NormalizeAmplitude(energies[9] * (vz::kSpectralPreGain * 4.2));
    double air = vz::NormalizeAmplitude(energies[10] * (vz::kSpectralPreGain * 5.0));
    double rms = static_cast<double>(rms_normalized);

    const double peak = std::max({ sub_bass, bass, low_mid, mid, high_mid, high, kick, snare, rms });
    io_state->agc_peak = std::max(peak, io_state->agc_peak * vz::kAgcRelease);
    double agc_scale = 1.0 / std::max(vz::kAgcFloor, io_state->agc_peak);

    if (io_state->agc_peak < 0.05) {
        const double quietness = 1.0 - (io_state->agc_peak / 0.05);
        agc_scale *= 1.0 + (quietness * 1.2);
    }

    sub_bass = std::clamp(sub_bass * agc_scale, 0.0, 1.0);
    bass = std::clamp(bass * agc_scale, 0.0, 1.0);
    low_mid = std::clamp(low_mid * agc_scale, 0.0, 1.0);
    mid = std::clamp(mid * agc_scale, 0.0, 1.0);
    high_mid = std::clamp(high_mid * agc_scale, 0.0, 1.0);
    high = std::clamp(high * agc_scale, 0.0, 1.0);
    kick = std::clamp(kick * agc_scale, 0.0, 1.0);
    snare = std::clamp(snare * agc_scale, 0.0, 1.0);
    melody = std::clamp(melody * agc_scale, 0.0, 1.0);
    hat = std::clamp(hat * agc_scale, 0.0, 1.0);
    air = std::clamp(air * agc_scale, 0.0, 1.0);
    rms = std::clamp(rms * agc_scale, 0.0, 1.0);

    const double post_agc_max = std::max({ sub_bass, bass, low_mid, mid, high_mid, high });
    const double post_agc_min = std::min({ sub_bass, bass, low_mid, mid, high_mid, high });
    const double post_agc_spread = post_agc_max - post_agc_min;
    if (post_agc_spread < 0.15 && post_agc_max > 0.5) {
        const double flatness = 1.0 - (post_agc_spread / 0.15);
        const double loudness = std::clamp((post_agc_max - 0.5) / 0.5, 0.0, 1.0);
        const double spread_gain = flatness * loudness * 2.5;
        const double mean = (sub_bass + bass + low_mid + mid + high_mid + high) / 6.0;
        sub_bass = std::clamp(mean + (sub_bass - mean) * (1.0 + spread_gain), 0.0, 1.0);
        bass = std::clamp(mean + (bass - mean) * (1.0 + spread_gain), 0.0, 1.0);
        low_mid = std::clamp(mean + (low_mid - mean) * (1.0 + spread_gain), 0.0, 1.0);
        mid = std::clamp(mean + (mid - mean) * (1.0 + spread_gain), 0.0, 1.0);
        high_mid = std::clamp(mean + (high_mid - mean) * (1.0 + spread_gain), 0.0, 1.0);
        high = std::clamp(mean + (high - mean) * (1.0 + spread_gain), 0.0, 1.0);
    }

    const double non_bass = (mid * 0.7) + (high * 0.6) + 1e-5;
    const double bass_dominance = (sub_bass + bass) / non_bass;
    const double bass_excess = std::clamp((bass_dominance - vz::kBassDominanceStart) / vz::kBassDominanceSpan, 0.0, 1.0);
    const double bass_scale = 1.0 - (bass_excess * (vz::kMaxLowAttenuation * 0.2));
    const double kick_scale = 1.0 - (bass_excess * (vz::kMaxKickAttenuation * 0.2));
    sub_bass *= bass_scale;
    bass *= bass_scale;
    kick *= kick_scale;

    const double high_boost = 1.0 + (bass_excess * 0.7);
    high_mid *= high_boost;
    high *= high_boost;

    const double kick_delta = std::max(0.0, kick - io_state->prev_kick_energy);
    const double snare_delta = std::max(0.0, snare - io_state->prev_snare_energy);
    const double rms_delta = std::max(0.0, rms - io_state->prev_rms_for_beat);
    io_state->prev_kick_energy = kick;
    io_state->prev_snare_energy = snare;
    io_state->prev_rms_for_beat = rms;

    const double kick_hit = std::clamp((kick_delta - vz::kKickTransientThreshold) * vz::kKickTransientGain, 0.0, 1.0);
    const double snare_hit = std::clamp((snare_delta - vz::kSnareTransientThreshold) * vz::kSnareTransientGain, 0.0, 1.0);
    const double rms_hit = std::clamp((rms_delta - vz::kBeatRmsDeltaThreshold) * 18.0, 0.0, 1.0);
    io_state->kick_accent = std::max(kick_hit, io_state->kick_accent * vz::kKickAccentDecay);
    io_state->snare_accent = std::max(snare_hit, io_state->snare_accent * vz::kSnareAccentDecay);
    const double beat_driver = (io_state->kick_accent * 0.55) + (io_state->snare_accent * 0.30) + (rms_hit * 0.35);
    const double beat_hit = std::clamp((beat_driver - vz::kBeatTransientThreshold) * vz::kBeatTransientGain, 0.0, 1.0);
    io_state->beat_accent = std::max(beat_hit, io_state->beat_accent * vz::kBeatAccentDecay);

    vz::ApplySpectralContrast(sub_bass, bass, low_mid, mid, high_mid, high, kick, snare, rms);
    melody = vz::ExpandDynamicRange(melody);
    hat = vz::ExpandDynamicRange(hat);
    air = vz::ExpandDynamicRange(air);

    const double groove_lift = rms * 0.05;
    out_targets[0] = static_cast<float>(std::clamp((kick * 0.48) + (sub_bass * 0.30) + (bass * 0.12) + groove_lift + (io_state->kick_accent * 0.26), 0.0, 1.0));
    out_targets[1] = static_cast<float>(std::clamp((snare * 0.48) + (high_mid * 0.20) + (mid * 0.14) + (low_mid * 0.08) + (io_state->snare_accent * 0.28), 0.0, 1.0));
    out_targets[2] = static_cast<float>(std::clamp((melody * 0.46) + (mid * 0.26) + (low_mid * 0.18) + (rms * 0.10), 0.0, 1.0));
    out_targets[3] = static_cast<float>(std::clamp((hat * 0.52) + (high * 0.20) + (snare * 0.12) + (high_mid * 0.10) + (io_state->snare_accent * 0.12), 0.0, 1.0));
    out_targets[4] = static_cast<float>(std::clamp((air * 0.54) + (high * 0.22) + (hat * 0.12) + (rms * 0.04), 0.0, 1.0));

    vz::ApplyInstrumentRoleResponse(out_targets, io_state->role_peaks, rms);
    vz::ApplyBarContrast(out_targets);
    vz::NormalizeDisplayTargetsToMaxVisual(out_targets);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_compute_hashes(
    const char16_t* sid,
    int32_t sid_len,
    uint32_t* out_no_audio_hash,
    uint32_t* out_floor_hash) noexcept {
    if (!out_no_audio_hash || !out_floor_hash || sid_len < 0) return VN_ERR_INVALID_ARG;

    uint32_t base_hash = 2166136261u;
    if (sid != nullptr) {
        for (int32_t i = 0; i < sid_len; ++i) {
            base_hash = (base_hash ^ static_cast<uint32_t>(sid[i])) * 16777619u;
        }
    }

    constexpr std::array<char16_t, 6> kFloorPrefix = { u'f', u'l', u'o', u'o', u'r', u':' };
    uint32_t floor_base_hash = 2166136261u;
    for (char16_t c : kFloorPrefix) {
        floor_base_hash = (floor_base_hash ^ static_cast<uint32_t>(c)) * 16777619u;
    }
    if (sid != nullptr) {
        for (int32_t i = 0; i < sid_len; ++i) {
            floor_base_hash = (floor_base_hash ^ static_cast<uint32_t>(sid[i])) * 16777619u;
        }
    }

    for (int32_t i = 0; i < vz::kBarCount; ++i) {
        out_no_audio_hash[i] = vz::ContinueHashInt(base_hash, i);
        out_floor_hash[i] = vz::ContinueHashInt(floor_base_hash, i);
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_step_spring(
    double* io_value,
    double* io_velocity,
    double target,
    double dt) noexcept {
    if (!io_value || !io_velocity) return VN_ERR_INVALID_ARG;
    constexpr double kFrequency = 24.0;
    const double offset = *io_value - target;
    const double impulse = *io_velocity + kFrequency * offset;
    const double decay = std::exp(-kFrequency * dt);
    *io_value = target + (offset + impulse * dt) * decay;
    *io_velocity = (*io_velocity - kFrequency * impulse * dt) * decay;
    if (std::abs(*io_value - target) < 0.001 && std::abs(*io_velocity) < 0.01) {
        *io_value = target;
        *io_velocity = 0.0;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_update_animation(
    int32_t state,
    double dt,
    double total_sec,
    uint8_t has_fresh_audio,
    double beat_accent,
    const float* levels,
    const uint32_t* no_audio_hash,
    const uint32_t* floor_hash,
    double* io_current_opacity,
    double* io_current_heights,
    uint8_t* out_is_settled) noexcept {
    if (!levels || !no_audio_hash || !floor_hash || !io_current_opacity || !io_current_heights || !out_is_settled) {
        return VN_ERR_INVALID_ARG;
    }

    bool is_settled = true;
    double target_opacity = 0.2;
    if (state == 2) target_opacity = 0.5;
    else if (state == 1 || state == 3) target_opacity = 1.0;

    *io_current_opacity += (target_opacity - *io_current_opacity) * (1.0 - std::exp(-dt * 1000.0 / vz::kTauOpacity));

    double audio_energy = 0.0;
    for (int32_t i = 0; i < vz::kBarCount; ++i) {
        audio_energy += std::clamp(static_cast<double>(levels[i]), 0.0, 1.0);
    }
    audio_energy = std::clamp(std::pow(audio_energy / vz::kBarCount, 0.85), 0.0, 1.0);

    const bool has_audible_energy = (has_fresh_audio != 0) && (audio_energy > vz::kAudioPresenceThreshold);
    const bool can_drive_from_audio = has_audible_energy && (state == 1 || state == 2);

    for (int32_t i = 0; i < vz::kBarCount; ++i) {
        double target_h = vz::kMinHeightRatio;

        if (can_drive_from_audio) {
            const double band = std::clamp(static_cast<double>(levels[i]), 0.0, 1.0);
            const double cross_band_lift = std::clamp(audio_energy * vz::kAudioReactiveCrossBandLift, 0.0, 0.16);
            const double audio_shaped = std::pow(std::clamp(band + cross_band_lift, 0.0, 1.0), 0.88);
            const double rhythm = vz::GetAudioReactiveRhythmAt(no_audio_hash[i], total_sec, audio_energy);
            const double rhythm_mix_base = vz::kLegacyRhythmMinMix + ((1.0 - audio_energy) * (vz::kLegacyRhythmMaxMix - vz::kLegacyRhythmMinMix));
            const double rhythm_mix = std::clamp(
                rhythm_mix_base + (vz::kAudioReactiveRhythmPush * (0.35 + audio_energy)),
                0.0,
                0.30);
            double normalized = (audio_shaped * (1.0 - rhythm_mix)) + (rhythm * rhythm_mix);
            normalized += (rhythm - 0.5) * (0.08 + (audio_energy * 0.08));
            normalized = std::max(normalized, vz::GetAudioReactiveFloor(i, floor_hash[i], total_sec, audio_energy, beat_accent));
            normalized = std::clamp(normalized + (beat_accent * vz::GetBeatLiftWeight(i) * 0.42), 0.0, 1.0);
            normalized = vz::ApplyBarPersonality(i, normalized, audio_energy);
            normalized = vz::ApplyMotionContrast(normalized);
            target_h = vz::MapNormalizedToHeight(normalized);
        } else if (state == 1 || state == 3) {
            const double normalized = vz::ApplyMotionContrast(vz::GetNoAudioPulseAt(no_audio_hash[i], total_sec));
            target_h = vz::MapNormalizedToHeight(normalized);
        }

        const double dynamic_release = std::max(0.50, vz::kAlphaRelease - (beat_accent * 0.08));
        double base_alpha = vz::kAlphaAttack;
        if (target_h <= io_current_heights[i]) {
            if (state == 2) {
                base_alpha = vz::kAlphaPauseRelease;
            } else {
                const double fall_ratio = std::clamp(
                    (io_current_heights[i] - target_h) / (vz::kMaxHeightRatio - vz::kMinHeightRatio),
                    0.0, 1.0);
                base_alpha = std::max(vz::kMinReleaseAlpha, dynamic_release - (fall_ratio * vz::kDownwardDropBoost));
            }
        }

        const double small_bar_ref = std::max(io_current_heights[i], target_h);
        if (small_bar_ref < vz::kSmallBarHeightThreshold && state != 2) {
            const double smallness = 1.0 - (small_bar_ref / vz::kSmallBarHeightThreshold);
            if (std::abs(target_h - io_current_heights[i]) < vz::kSmallBarTargetDeadzone * (1.0 + smallness)) {
                target_h = io_current_heights[i];
            }
        }

        const double dt_ms = dt * 1000.0;
        const double alpha = std::pow(base_alpha, dt_ms / vz::kReferenceFrameMs);

        const double old_h = io_current_heights[i];
        const double new_h = (io_current_heights[i] * alpha) + (target_h * (1.0 - alpha));
        io_current_heights[i] = (std::abs(target_h - new_h) <= vz::kMinHeightChangeThreshold) ? target_h : new_h;

        if (std::abs(io_current_heights[i] - old_h) > 0.001) {
            is_settled = false;
        }
    }

    *out_is_settled = is_settled ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_visualizer_prepare_draw_heights(
    double last_dt_ms,
    const double* current_heights,
    double* io_smoothed_heights,
    double* out_sorted_heights,
    double* out_draw_heights) noexcept {
    if (!current_heights || !io_smoothed_heights || !out_sorted_heights || !out_draw_heights) {
        return VN_ERR_INVALID_ARG;
    }

    const double smoothing_factor = std::pow(0.56, last_dt_ms / vz::kReferenceFrameMs);
    const double small_bar_smoothing_delta = std::pow(0.58, last_dt_ms / vz::kReferenceFrameMs) - smoothing_factor;
    for (int32_t i = 0; i < vz::kBarCount; ++i) {
        double target_smoothing = smoothing_factor;
        const double ref_height = std::max(io_smoothed_heights[i], current_heights[i]);
        if (ref_height < vz::kSmallBarHeightThreshold) {
            const double smallness = 1.0 - (ref_height / vz::kSmallBarHeightThreshold);
            target_smoothing = std::min(0.78, smoothing_factor + (small_bar_smoothing_delta * smallness));
        }
        io_smoothed_heights[i] = (io_smoothed_heights[i] * target_smoothing) + (current_heights[i] * (1.0 - target_smoothing));
        out_sorted_heights[i] = io_smoothed_heights[i];
    }

    for (int32_t i = 0; i < vz::kBarCount - 1; ++i) {
        for (int32_t j = i + 1; j < vz::kBarCount; ++j) {
            if (out_sorted_heights[j] > out_sorted_heights[i]) {
                std::swap(out_sorted_heights[i], out_sorted_heights[j]);
            }
        }
    }

    const double max_v = out_sorted_heights[0];
    const double min_v = out_sorted_heights[vz::kBarCount - 1];
    const double spread = std::clamp(max_v - min_v, 0.0, 1.0);
    double bias = vz::kRightBiasStrength;
    if (spread < vz::kRightBiasDeadzone) {
        bias *= (spread / vz::kRightBiasDeadzone);
    }

    for (int32_t i = 0; i < vz::kBarCount; ++i) {
        out_draw_heights[i] = (io_smoothed_heights[i] * (1.0 - bias)) + (out_sorted_heights[i] * bias);
    }
    return VN_OK;
}

} // extern "C"
