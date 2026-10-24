#include "../vnotch_common.h"
#include <mmsystem.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <vector>

#pragma comment(lib, "winmm.lib")

// Interaction sound pacing and playback. The gate keeps the same anti-spam rules as before (global gate,
// per-cue cooldown, burst back-off, no immediate repeat of a variant); playback gain is applied to the PCM
// samples only, never to the system or media volume.

extern "C" int32_t vn_interaction_sounds_create_wave(int32_t cue, int32_t variant, uint8_t* out_buf, int32_t out_cap, int32_t* out_len);

namespace vnotch::services::interaction {

constexpr int32_t kCueCount = 10;
constexpr int32_t kVariantCount = 6;
constexpr int32_t kWaveHeaderBytes = 44;
constexpr int32_t kWaveCapacity = 8192;
constexpr int64_t kGlobalGateMs = 65;
constexpr int64_t kBurstWindowMs = 220;
constexpr int32_t kBurstMax = 6;
constexpr int32_t kBurstSuppressFrom = 3;
constexpr int64_t kBurstStepMs = 35;
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
        case kSlider: return 130;
        case kConfirm:
        case kDelete: return 180;
        case kToggleOn:
        case kToggleOff:
        case kNavigate: return 140;
        default: return 110;
    }
}

SRWLOCK g_play_lock = SRWLOCK_INIT;
std::array<std::vector<uint8_t>, 2> g_buffers;
int g_next_buffer = 0;

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

// Decides whether a cue may play now. random supplies the entropy for the variant pick. When allowed, the
// state records the play. out_burst_count feeds the playback ducking.
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

// Plays cue/variant asynchronously, replacing any sound still playing. volume is 0..1; ducking follows burst_count.
VN_EXPORT int32_t __stdcall vn_interaction_play(int32_t cue, int32_t variant, float volume, int32_t burst_count) noexcept {
    if (cue < 0 || cue >= ia::kCueCount || variant < 0 || variant >= ia::kVariantCount || !std::isfinite(volume)) return VN_ERR_INVALID_ARG;
    uint8_t wave[ia::kWaveCapacity];
    int32_t length = 0;
    if (vn_interaction_sounds_create_wave(cue, variant, wave, static_cast<int32_t>(sizeof(wave)), &length) != VN_OK ||
        length <= ia::kWaveHeaderBytes || length > ia::kWaveCapacity)
        return VN_ERR_FAIL;

    const float duck = burst_count > 0 ? std::max(0.45f, 1.0f - static_cast<float>(burst_count) * 0.12f) : 1.0f;
    const float gain = std::clamp(volume, 0.0f, 1.0f) * duck;
    auto* samples = reinterpret_cast<int16_t*>(wave + ia::kWaveHeaderBytes);
    const int32_t sample_count = (length - ia::kWaveHeaderBytes) / static_cast<int32_t>(sizeof(int16_t));
    for (int32_t i = 0; i < sample_count; ++i)
        samples[i] = static_cast<int16_t>(std::clamp(std::lrintf(static_cast<float>(samples[i]) * gain), -32768L, 32767L));

    try {
        AcquireSRWLockExclusive(&ia::g_play_lock);
        std::vector<uint8_t>& buffer = ia::g_buffers[static_cast<size_t>(ia::g_next_buffer)];
        ia::g_next_buffer ^= 1;
        buffer.assign(wave, wave + length);
        // SND_MEMORY|SND_ASYNC reads the image while playing, so the buffer stays alive until the next play.
        const BOOL started = PlaySoundW(reinterpret_cast<LPCWSTR>(buffer.data()), nullptr, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        ReleaseSRWLockExclusive(&ia::g_play_lock);
        return started ? VN_OK : VN_ERR_FAIL;
    } catch (...) {
        ReleaseSRWLockExclusive(&ia::g_play_lock);
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_interaction_stop() noexcept {
    PlaySoundW(nullptr, nullptr, 0);
    return VN_OK;
}

} // extern "C"
