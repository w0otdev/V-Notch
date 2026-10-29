#include "../vnotch_common.h"
#include <mmsystem.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>

#pragma comment(lib, "winmm.lib")

// Interaction sound pacing and low-latency waveOut playback. All 60 cue/variant waveforms are synthesized once
// into a static PCM table, and a persistent HWAVEOUT stream with pre-prepared WAVEHDR slots submits buffers
// directly without PlaySoundW thread/header marshalling overhead or cold-start clipping.

extern "C" int32_t vn_interaction_sounds_create_wave(int32_t cue, int32_t variant, uint8_t* out_buf, int32_t out_cap, int32_t* out_len);

namespace vnotch::services::interaction {

constexpr int32_t kCueCount = 10;
constexpr int32_t kVariantCount = 6;
constexpr int32_t kWaveHeaderBytes = 44;
constexpr int32_t kWaveCapacity = 16384;
constexpr int32_t kMaxSamples = (kWaveCapacity - kWaveHeaderBytes) / 2;
constexpr int32_t kSampleRate = 44100;
constexpr int32_t kVoiceSlots = 4;

constexpr int64_t kGlobalGateMs = 65;
constexpr int64_t kBurstWindowMs = 120;
constexpr int32_t kBurstMax = 6;
constexpr int32_t kBurstSuppressFrom = 4;
constexpr int64_t kBurstStepMs = 20;
constexpr int64_t kNeverPlayed = INT64_MIN / 2;

enum Cue : int32_t { kClick, kToggleOn, kToggleOff, kNavigate, kSelect, kSlider, kMenu, kConfirm, kDelete, kTransport };

#pragma pack(push, 1)
struct VnInteractionGateState {
    int64_t last_played;
    uint8_t has_last;
    int64_t last_cue_played[kCueCount];
    int32_t last_variants[kCueCount];
    int32_t rapid_burst_count;
};
#pragma pack(pop)

constexpr int64_t SameCueCooldownMs(int32_t cue, bool has_override) noexcept {
    if (has_override) return kGlobalGateMs;
    switch (cue) {
        case kSlider: return 95;
        case kConfirm:
        case kDelete: return 140;
        case kToggleOn:
        case kToggleOff:
        case kNavigate: return 105;
        default: return 85;
    }
}

struct CachedWave {
    int16_t samples[kMaxSamples];
    int32_t sample_count;
};

INIT_ONCE g_cache_once = INIT_ONCE_STATIC_INIT;
CachedWave g_wave_cache[kCueCount][kVariantCount]{};

BOOL CALLBACK InitWaveCache(PINIT_ONCE, PVOID, PVOID*) noexcept {
    static uint8_t raw[kWaveCapacity];
    for (int32_t c = 0; c < kCueCount; ++c) {
        for (int32_t v = 0; v < kVariantCount; ++v) {
            int32_t len = 0;
            if (vn_interaction_sounds_create_wave(c, v, raw, kWaveCapacity, &len) == VN_OK && len > kWaveHeaderBytes) {
                const int32_t count = std::min(kMaxSamples, (len - kWaveHeaderBytes) / 2);
                std::memcpy(g_wave_cache[c][v].samples, raw + kWaveHeaderBytes, static_cast<size_t>(count) * sizeof(int16_t));
                g_wave_cache[c][v].sample_count = count;
            }
        }
    }
    return TRUE;
}

struct VoiceSlot {
    WAVEHDR header{};
    int16_t pcm[kMaxSamples]{};
    bool prepared = false;
};

SRWLOCK g_play_lock = SRWLOCK_INIT;
HWAVEOUT g_wave_out = nullptr;
std::array<VoiceSlot, kVoiceSlots> g_slots{};
uint32_t g_next_slot = 0;

bool EnsureWaveOutLocked() noexcept {
    if (g_wave_out != nullptr) return true;
    WAVEFORMATEX fmt{};
    fmt.wFormatTag = WAVE_FORMAT_PCM;
    fmt.nChannels = 1;
    fmt.nSamplesPerSec = kSampleRate;
    fmt.wBitsPerSample = 16;
    fmt.nBlockAlign = 2;
    fmt.nAvgBytesPerSec = kSampleRate * 2;
    if (waveOutOpen(&g_wave_out, WAVE_MAPPER, &fmt, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR) {
        g_wave_out = nullptr;
        return false;
    }
    return true;
}

void ResetAndCloseLocked() noexcept {
    if (!g_wave_out) return;
    waveOutReset(g_wave_out);
    for (auto& slot : g_slots) {
        if (slot.prepared) {
            waveOutUnprepareHeader(g_wave_out, &slot.header, sizeof(WAVEHDR));
            slot.prepared = false;
        }
    }
    waveOutClose(g_wave_out);
    g_wave_out = nullptr;
}

} // namespace vnotch::services::interaction

namespace ia = vnotch::services::interaction;

extern "C" {

VN_EXPORT int32_t __stdcall vn_interaction_gate_reset(ia::VnInteractionGateState* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    *state = {};
    state->has_last = 0;
    std::fill(std::begin(state->last_cue_played), std::end(state->last_cue_played), ia::kNeverPlayed);
    std::fill(std::begin(state->last_variants), std::end(state->last_variants), -1);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_interaction_gate_evaluate(
    ia::VnInteractionGateState* state, int32_t cue, int64_t now_ms, uint8_t has_override, uint32_t random,
    uint8_t* out_allow, int32_t* out_variant, int32_t* out_burst_count) noexcept {
    if (!state || !out_allow || !out_variant || !out_burst_count || cue < 0 || cue >= ia::kCueCount) return VN_ERR_INVALID_ARG;
    *out_allow = 0;
    *out_variant = 0;
    *out_burst_count = state->rapid_burst_count;
    const bool override_play = has_override != 0;

    if (state->has_last != 0) {
        const int64_t elapsed = now_ms - state->last_played;
        if (elapsed < ia::kGlobalGateMs) return VN_OK;
        const int64_t cue_elapsed = now_ms - state->last_cue_played[cue];
        if (cue_elapsed < ia::SameCueCooldownMs(cue, override_play)) return VN_OK;
        state->rapid_burst_count = elapsed < ia::kBurstWindowMs ? std::min(ia::kBurstMax, state->rapid_burst_count + 1) : 0;
        *out_burst_count = state->rapid_burst_count;
        if (!override_play && state->rapid_burst_count >= ia::kBurstSuppressFrom &&
            elapsed < ia::kGlobalGateMs + state->rapid_burst_count * ia::kBurstStepMs)
            return VN_OK;
    }

    state->last_played = now_ms;
    state->has_last = 1;
    state->last_cue_played[cue] = now_ms;
    const int32_t previous = state->last_variants[cue];
    int32_t variant = static_cast<int32_t>(random % static_cast<uint32_t>(previous < 0 ? ia::kVariantCount : ia::kVariantCount - 1));
    if (previous >= 0 && variant >= previous) ++variant;
    state->last_variants[cue] = variant;
    *out_variant = variant;
    *out_allow = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_interaction_play(int32_t cue, int32_t variant, float volume, int32_t burst_count) noexcept {
    if (cue < 0 || cue >= ia::kCueCount || variant < 0 || variant >= ia::kVariantCount || !std::isfinite(volume)) return VN_ERR_INVALID_ARG;

    InitOnceExecuteOnce(&ia::g_cache_once, ia::InitWaveCache, nullptr, nullptr);
    const ia::CachedWave& cached = ia::g_wave_cache[cue][variant];
    if (cached.sample_count <= 0) return VN_ERR_FAIL;

    const float duck = burst_count > 0 ? std::max(0.82f, 1.0f - static_cast<float>(burst_count) * 0.035f) : 1.0f;
    const float gain = std::clamp(volume, 0.0f, 1.0f) * duck;

    AcquireSRWLockExclusive(&ia::g_play_lock);
    if (!ia::EnsureWaveOutLocked()) {
        ReleaseSRWLockExclusive(&ia::g_play_lock);
        return VN_ERR_FAIL;
    }

    ia::VoiceSlot* chosen = nullptr;
    for (uint32_t attempt = 0; attempt < ia::kVoiceSlots; ++attempt) {
        ia::VoiceSlot& candidate = ia::g_slots[(ia::g_next_slot + attempt) % ia::kVoiceSlots];
        if (!candidate.prepared || (candidate.header.dwFlags & WHDR_DONE) != 0) {
            chosen = &candidate;
            ia::g_next_slot = (ia::g_next_slot + attempt + 1) % ia::kVoiceSlots;
            break;
        }
    }

    if (!chosen) {
        waveOutReset(ia::g_wave_out);
        chosen = &ia::g_slots[ia::g_next_slot % ia::kVoiceSlots];
        ia::g_next_slot = (ia::g_next_slot + 1) % ia::kVoiceSlots;
    }

    if (chosen->prepared) {
        waveOutUnprepareHeader(ia::g_wave_out, &chosen->header, sizeof(WAVEHDR));
        chosen->prepared = false;
    }

    const int32_t count = cached.sample_count;
    for (int32_t i = 0; i < count; ++i) {
        chosen->pcm[i] = static_cast<int16_t>(std::clamp(std::lrintf(static_cast<float>(cached.samples[i]) * gain), -32768L, 32767L));
    }

    chosen->header = {};
    chosen->header.lpData = reinterpret_cast<LPSTR>(chosen->pcm);
    chosen->header.dwBufferLength = static_cast<DWORD>(count * sizeof(int16_t));

    MMRESULT mm = waveOutPrepareHeader(ia::g_wave_out, &chosen->header, sizeof(WAVEHDR));
    if (mm == MMSYSERR_NOERROR) {
        chosen->prepared = true;
        mm = waveOutWrite(ia::g_wave_out, &chosen->header, sizeof(WAVEHDR));
    }

    if (mm != MMSYSERR_NOERROR) {
        ia::ResetAndCloseLocked();
        ReleaseSRWLockExclusive(&ia::g_play_lock);
        return VN_ERR_FAIL;
    }

    ReleaseSRWLockExclusive(&ia::g_play_lock);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_interaction_stop() noexcept {
    AcquireSRWLockExclusive(&ia::g_play_lock);
    if (ia::g_wave_out != nullptr) {
        waveOutReset(ia::g_wave_out);
    }
    ReleaseSRWLockExclusive(&ia::g_play_lock);
    return VN_OK;
}

} // extern "C"
