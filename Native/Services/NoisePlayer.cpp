// Procedural ambient noise player. Streams generated audio straight to waveOut, so there are
// no sound files, no loop seams and nothing to download. Output is 44.1 kHz stereo with
// decorrelated channels, fading in/out and smooth volume changes to avoid clicks.
#include "../vnotch_common.h"
#include <mmsystem.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

#pragma comment(lib, "winmm.lib")

namespace {

constexpr int32_t kSampleRate = 44100;
constexpr int32_t kFramesPerBuffer = 1764;  // 40 ms
constexpr int32_t kBufferCount = 4;
constexpr float kPi = 3.14159265358979f;
constexpr int32_t kKindCount = 8;

enum Kind : int32_t { White, Pink, Brown, Fan, Rain, Ocean, Wind, Stream };

inline float OnePoleCoef(float cutoffHz) noexcept {
    return 1.0f - std::exp(-2.0f * kPi * cutoffHz / static_cast<float>(kSampleRate));
}

template <int32_t CutoffMilliHz>
inline float Coef() noexcept {
    static const float c = OnePoleCoef(static_cast<float>(CutoffMilliHz) * 0.001f);
    return c;
}

inline float SoftLimit(float x) noexcept {
    // Smooth, symmetric, never exceeds +/-1 and stays linear for small signals.
    return x / (1.0f + std::fabs(x));
}

struct Voice {
    uint64_t rng = 0x9E3779B97F4A7C15ull;
    float pink0 = 0, pink1 = 0, pink2 = 0;
    float brown = 0;
    float lp1 = 0, lp2 = 0, hp1 = 0;
    float svfLow = 0, svfBand = 0;
    float slowA = 0, slowB = 0;
    float lfoPhase = 0, lfoRate = 0;
    float humPhase = 0, bladePhase = 0;
    float dropEnv = 0, d1 = 0, d2 = 0;

    void Seed(uint64_t seed) noexcept {
        *this = Voice{};
        rng ^= seed * 0xD1B54A32D192ED03ull;
        if (rng == 0) rng = 0x9E3779B97F4A7C15ull;
        lfoPhase = Uniform() * kPi;
        lfoRate = 2.0f * kPi / (9.0f * static_cast<float>(kSampleRate));
    }

    float White() noexcept {
        rng ^= rng >> 12;
        rng ^= rng << 25;
        rng ^= rng >> 27;
        const uint64_t v = rng * 0x2545F4914F6CDD1Dull;
        return static_cast<float>(static_cast<int32_t>(v >> 32)) * (1.0f / 2147483648.0f);
    }

    float Uniform() noexcept { return White() * 0.5f + 0.5f; }

    float Pink(float w) noexcept {
        pink0 = 0.99765f * pink0 + w * 0.0990460f;
        pink1 = 0.96300f * pink1 + w * 0.2965164f;
        pink2 = 0.57000f * pink2 + w * 1.0526913f;
        return (pink0 + pink1 + pink2 + w * 0.1848f) * 0.11f;
    }

    // Smoothed random walk in [-1, 1] changing at roughly `rateHz`.
    float Wander(float& state, float coef) noexcept {
        state += coef * (White() * 2.2f - state);
        return std::clamp(state, -1.0f, 1.0f);
    }

    float Next(int32_t kind) noexcept {
        const float w = White();
        switch (kind) {
        case Kind::White: {
            // Gentle top roll-off keeps the hiss full but not piercing.
            lp1 += Coef<6500000>() * (w - lp1);
            lp2 += Coef<6500000>() * (lp1 - lp2);
            return lp2 * 0.35f;
        }
        case Kind::Pink: {
            lp1 += Coef<5500000>() * (Pink(w) - lp1);
            return lp1 * 1.2f;
        }
        case Kind::Brown:
            brown = 0.998f * brown + 0.04f * w;
            return brown * 0.42f;
        case Kind::Fan: {
            // Low, round air flow with a faint motor hum and blade flutter.
            lp1 += Coef<900000>() * (Pink(w) * 1.6f - lp1);
            lp2 += Coef<260000>() * (lp1 - lp2);
            humPhase += 2.0f * kPi * 96.0f / static_cast<float>(kSampleRate);
            if (humPhase > 2.0f * kPi) humPhase -= 2.0f * kPi;
            bladePhase += 2.0f * kPi * 27.0f / static_cast<float>(kSampleRate);
            if (bladePhase > 2.0f * kPi) bladePhase -= 2.0f * kPi;
            const float hum = 0.05f * std::sin(humPhase) + 0.022f * std::sin(2.0f * humPhase) + 0.01f * std::sin(3.0f * humPhase);
            const float flutter = 1.0f + 0.05f * std::sin(bladePhase);
            return (lp1 * 0.75f + lp2 * 0.6f) * flutter + hum;
        }
        case Kind::Rain: {
            // Dense, steady shower: a pink-ish band bed, a faint distant rumble and a thick
            // layer of tiny droplet bursts. No isolated taps, which sound artificial.
            const float pk = Pink(w);
            hp1 += Coef<350000>() * (pk - hp1);
            lp1 += Coef<5000000>() * ((pk - hp1) - lp1);
            lp2 += Coef<5000000>() * (lp1 - lp2);
            brown = 0.998f * brown + 0.04f * w;
            const float roll = 0.85f + 0.15f * Wander(slowA, Coef<300>());
            if (Uniform() < 0.012f) dropEnv = 0.25f + 0.75f * Uniform();
            dropEnv *= 0.994f;
            const float x = White() * dropEnv;
            d1 += Coef<3800000>() * (x - d1);
            d2 += Coef<1200000>() * (d1 - d2);
            const float drops = (d1 - d2) * 1.1f;
            return (lp2 * 1.25f + drops * 0.55f + brown * 0.12f) * roll;
        }
        case Kind::Ocean: {
            brown = 0.998f * brown + 0.04f * w;
            lp1 += Coef<700000>() * (brown * 1.2f - lp1);
            lfoPhase += lfoRate * (1.0f + 0.35f * Wander(slowA, Coef<70>()));
            if (lfoPhase > 2.0f * kPi) lfoPhase -= 2.0f * kPi;
            const float swell = 0.5f + 0.5f * std::sin(lfoPhase);
            const float env = 0.25f + 0.75f * swell * swell;
            lp2 += Coef<3500000>() * (w - lp2);
            const float hiss = (w - lp2) * 0.06f * swell * swell * swell;
            return (lp1 * 0.6f + hiss) * env * 1.4f;
        }
        case Kind::Wind: {
            const float g = Wander(slowA, Coef<350>());
            const float fc = 450.0f + 350.0f * g;
            const float f = 2.0f * std::sin(kPi * fc / static_cast<float>(kSampleRate));
            const float q = 0.45f;
            svfLow += f * svfBand;
            const float high = w * 0.35f - svfLow - q * svfBand;
            svfBand += f * high;
            const float gain = 0.65f + 0.35f * Wander(slowB, Coef<200>());
            return svfBand * gain * 0.85f;
        }
        case Kind::Stream: {
            // Mid-range water body with bubbling resonances; nothing above ~3 kHz.
            lp1 += Coef<1500000>() * (w - lp1);
            hp1 += Coef<250000>() * (lp1 - hp1);
            const float body = lp1 - hp1;
            const float g = Wander(slowA, Coef<12000>());
            const float fc = 750.0f + 500.0f * g;
            const float f = 2.0f * std::sin(kPi * fc / static_cast<float>(kSampleRate));
            const float q = 0.25f;
            svfLow += f * svfBand;
            const float high = w * 0.25f - svfLow - q * svfBand;
            svfBand += f * high;
            const float bubble = Wander(slowB, Coef<8000>());
            const float gate = 0.35f + 0.65f * std::max(0.0f, bubble);
            lp2 += Coef<3000000>() * ((body * 0.9f + svfBand * 0.7f * gate) - lp2);
            return lp2;
        }
        default:
            return 0.0f;
        }
    }
};

// Precomputed 12-second RMS normalization gains (seed 77, target RMS 0.15 * kLoudnessWeight).
constexpr std::array<float, kKindCount> kNormGain = {
    1.173383f, 0.647081f, 0.993035f, 0.500040f,
    1.055131f, 0.622996f, 4.949397f, 1.020860f
};

constexpr float NormalizationGain(int32_t kind) noexcept {
    return (kind >= 0 && kind < kKindCount) ? kNormGain[static_cast<size_t>(kind)] : 1.0f;
}
struct PlayerState {
    std::mutex mutex;
    std::thread thread;
    bool running = false;  // guarded by mutex
    std::atomic<bool> wantOn{false};
    std::atomic<int32_t> wantKind{0};
    std::atomic<int32_t> currentKind{-1};
    std::atomic<float> wantVolume{0.3f};
    std::atomic<float> fadeOutSeconds{0.25f};

    ~PlayerState() noexcept {
        fadeOutSeconds.store(0.05f, std::memory_order_relaxed);
        wantOn.store(false, std::memory_order_release);
        std::thread toJoin;
        {
            std::lock_guard<std::mutex> lock(mutex);
            if (thread.joinable()) toJoin = std::move(thread);
        }
        if (toJoin.joinable()) {
            if (toJoin.get_id() == std::this_thread::get_id())
                toJoin.detach();
            else
                toJoin.join();
        }
    }
};

PlayerState g;

void FillBuffer(std::vector<int16_t>& out, Voice& left, Voice& right, int32_t kind,
                float norm, float& gain, float& volume, bool& silent) noexcept {
    const float volCoef = OnePoleCoef(12.0f);
    const float fadeUp = 1.0f / (0.25f * static_cast<float>(kSampleRate));
    const float fadeDown = 1.0f / (std::max(0.05f, g.fadeOutSeconds.load(std::memory_order_relaxed)) * static_cast<float>(kSampleRate));
    const bool on = g.wantOn.load(std::memory_order_relaxed);
    const float targetVol = g.wantVolume.load(std::memory_order_relaxed);
    const bool kindMatches = kind == g.wantKind.load(std::memory_order_relaxed);
    const float targetGain = (on && kindMatches) ? 1.0f : 0.0f;
    for (int32_t i = 0; i < kFramesPerBuffer; ++i) {
        volume += volCoef * (targetVol - volume);
        if (gain < targetGain) gain = std::min(targetGain, gain + fadeUp);
        else if (gain > targetGain) gain = std::max(targetGain, gain - fadeDown);
        const float scale = gain * volume * 0.5f;
        const float l = SoftLimit(left.Next(kind) * norm) * scale;
        const float r = SoftLimit(right.Next(kind) * norm) * scale;
        out[static_cast<size_t>(i) * 2] = static_cast<int16_t>(std::clamp(l, -1.0f, 1.0f) * 32767.0f);
        out[static_cast<size_t>(i) * 2 + 1] = static_cast<int16_t>(std::clamp(r, -1.0f, 1.0f) * 32767.0f);
    }
    silent = gain <= 0.0f && targetGain <= 0.0f;
}

using NoiseFailureCallback = void(__stdcall*)(int32_t kind, int32_t error_code);
std::atomic<NoiseFailureCallback> g_failure_callback{nullptr};

// Owns every native resource of one playback session. The destructor releases them in the order waveOut
// requires: stop and return queued buffers, unprepare only the headers that were prepared, close the
// device, close the event. Members are declared so the PCM storage outlives the device cleanup.
struct WaveSession {
    HANDLE event = nullptr;
    HWAVEOUT device = nullptr;
    std::array<std::vector<int16_t>, kBufferCount> pcm;
    std::array<WAVEHDR, kBufferCount> headers{};
    int32_t prepared = 0;

    WaveSession() = default;
    WaveSession(const WaveSession&) = delete;
    WaveSession& operator=(const WaveSession&) = delete;

    ~WaveSession() {
        if (device) {
            waveOutReset(device);
            for (int32_t i = prepared; i-- > 0;) waveOutUnprepareHeader(device, &headers[static_cast<size_t>(i)], sizeof(WAVEHDR));
            waveOutClose(device);
        }
        if (event) CloseHandle(event);
    }
};

// Plays until the sound has faded out and nobody wants it any more. Returns 0 for a normal end, otherwise the
// Win32 or MMSYSERR code of the failure. Every resource is released by WaveSession on every exit path.
int32_t RunPlayer() noexcept {
    try {
        WaveSession session;
        session.event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!session.event) return static_cast<int32_t>(GetLastError());

        WAVEFORMATEX fmt{};
        fmt.wFormatTag = WAVE_FORMAT_PCM;
        fmt.nChannels = 2;
        fmt.nSamplesPerSec = kSampleRate;
        fmt.wBitsPerSample = 16;
        fmt.nBlockAlign = 4;
        fmt.nAvgBytesPerSec = kSampleRate * 4;
        HWAVEOUT opened = nullptr;
        const MMRESULT openResult = waveOutOpen(&opened, WAVE_MAPPER, &fmt, reinterpret_cast<DWORD_PTR>(session.event), 0, CALLBACK_EVENT);
        if (openResult != MMSYSERR_NOERROR) return static_cast<int32_t>(openResult);
        session.device = opened;
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

        for (int32_t i = 0; i < kBufferCount; ++i) {
            const size_t index = static_cast<size_t>(i);
            session.pcm[index].assign(static_cast<size_t>(kFramesPerBuffer) * 2, 0);
            WAVEHDR& header = session.headers[index];
            header.lpData = reinterpret_cast<LPSTR>(session.pcm[index].data());
            header.dwBufferLength = static_cast<DWORD>(session.pcm[index].size() * sizeof(int16_t));
            const MMRESULT prepareResult = waveOutPrepareHeader(session.device, &header, sizeof(WAVEHDR));
            if (prepareResult != MMSYSERR_NOERROR) return static_cast<int32_t>(prepareResult);
            ++session.prepared;
            header.dwFlags |= WHDR_DONE;
        }

        Voice left, right;
        int32_t kind = g.wantKind.load();
        left.Seed(0x1234u + static_cast<uint64_t>(kind));
        right.Seed(0xABCDu + static_cast<uint64_t>(kind));
        g.currentKind = kind;
        float norm = NormalizationGain(kind);
        float gain = 0.0f;
        float volume = g.wantVolume.load();
        int32_t silentBuffers = 0;

        for (;;) {
            WaitForSingleObject(session.event, 100);
            for (int32_t i = 0; i < kBufferCount; ++i) {
                const size_t index = static_cast<size_t>(i);
                if (!(session.headers[index].dwFlags & WHDR_DONE)) continue;
                bool silent = false;
                FillBuffer(session.pcm[index], left, right, kind, norm, gain, volume, silent);
                const MMRESULT writeResult = waveOutWrite(session.device, &session.headers[index], sizeof(WAVEHDR));
                if (writeResult != MMSYSERR_NOERROR) return static_cast<int32_t>(writeResult);
                if (!silent) {
                    silentBuffers = 0;
                    continue;
                }
                ++silentBuffers;
                const int32_t wanted = g.wantKind.load();
                if (g.wantOn.load() && wanted != kind) {
                    // Faded out completely: switch the generator to the requested sound.
                    kind = wanted;
                    left.Seed(0x1234u + static_cast<uint64_t>(kind));
                    right.Seed(0xABCDu + static_cast<uint64_t>(kind));
                    g.currentKind = kind;
                    norm = NormalizationGain(kind);
                    silentBuffers = 0;
                }
            }
            if (silentBuffers < kBufferCount * 2 || g.wantOn.load()) continue;
            std::lock_guard lock(g.mutex);
            if (g.wantOn.load()) continue;
            g.running = false;
            return 0;
        }
    } catch (...) {
        return static_cast<int32_t>(ERROR_OUTOFMEMORY);
    }
}

void PlayerThread() noexcept {
    const int32_t error = RunPlayer();
    g.currentKind = -1;
    if (error == 0) return;

    // The play call already returned success, so tell the managed side that the sound is not playing.
    int32_t failedKind = -1;
    try {
        std::lock_guard lock(g.mutex);
        g.running = false;
        g.wantOn = false;
        failedKind = g.wantKind.load();
    } catch (...) {
        g.wantOn = false;
    }
    if (const NoiseFailureCallback callback = g_failure_callback.load()) callback(failedKind, error);
}
}  // namespace

VN_EXPORT int32_t __stdcall vn_noise_play(int32_t kind, float volume) noexcept {
    if (kind < 0 || kind >= kKindCount) return VN_ERR_INVALID_ARG;
    try {
        std::lock_guard lock(g.mutex);
        g.wantKind = kind;
        g.wantVolume = std::clamp(volume, 0.0f, 1.0f);
        g.fadeOutSeconds = 0.25f;
        g.wantOn = true;
        if (g.running) return VN_OK;
        if (g.thread.joinable()) g.thread.join();
        g.running = true;
        g.thread = std::thread(PlayerThread);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Registers a callback (any thread) raised when playback could not start or broke after vn_noise_play returned VN_OK.
// It receives the requested kind and the Win32/MMSYSERR code. Pass null to remove it.
VN_EXPORT int32_t __stdcall vn_noise_set_failure_callback(NoiseFailureCallback callback) noexcept {
    g_failure_callback.store(callback);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_noise_set_volume(float volume) noexcept {
    g.wantVolume = std::clamp(volume, 0.0f, 1.0f);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_noise_stop() noexcept {
    g.wantOn.store(false, std::memory_order_release);
    std::thread toJoin;
    {
        std::lock_guard lock(g.mutex);
        if (g.thread.joinable() && g.thread.get_id() != std::this_thread::get_id()) {
            toJoin = std::move(g.thread);
        }
    }
    if (toJoin.joinable()) {
        toJoin.join();
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_noise_stop_fade(float seconds) noexcept {
    g.fadeOutSeconds = std::clamp(seconds, 0.05f, 10.0f);
    g.wantOn = false;
    return VN_OK;
}
