#include "../vnotch_common.h"
#include "../vnotch_com.h"
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <mmsystem.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "ole32.lib")

// Spotlight click sound: the asset (m4a/mp3) is decoded through Media Foundation once, softened with a
// two-pole low-pass and normalized, then played with software gain. Nothing touches system volume.

namespace vnotch::services::spotlightsfx {

using vnotch::com::ComPtr;
using vnotch::com::Guard;

constexpr DWORD kFirstAudioStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
constexpr double kLowPassCoefficient = 0.26;
constexpr double kMaxNormalizeGain = 2.8;
constexpr double kNormalizeTarget = 22000.0;
constexpr int kPcmClamp = 28000;
constexpr size_t kMaxDecodedBytes = 16u * 1024u * 1024u;

SRWLOCK g_lock = SRWLOCK_INIT;
INIT_ONCE g_mf_once = INIT_ONCE_STATIC_INIT;
bool g_loaded = false;
uint32_t g_channels = 0;
uint32_t g_sample_rate = 0;
std::vector<int16_t> g_pcm;
HWAVEOUT g_wave_out = nullptr;
struct SfxSlot {
    WAVEHDR header{};
    std::vector<int16_t> pcm;
    bool prepared = false;
};
std::array<SfxSlot, 2> g_play{};
int g_next_play = 0;

BOOL CALLBACK StartMediaFoundation(PINIT_ONCE, PVOID, PVOID*) noexcept {
    MFStartup(MF_VERSION, MFSTARTUP_LITE);
    return TRUE;
}

// Decodes the file to interleaved 16-bit PCM.
HRESULT DecodePcm(const std::wstring& path, std::vector<uint8_t>& pcm, uint32_t& channels, uint32_t& sample_rate) {
    ComPtr<IMFSourceReader> reader;
    HRESULT hr = MFCreateSourceReaderFromURL(path.c_str(), nullptr, reader.Put());
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaType> request;
    hr = MFCreateMediaType(request.Put());
    if (FAILED(hr)) return hr;
    request.ptr->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    request.ptr->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
    request.ptr->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    hr = reader.ptr->SetCurrentMediaType(kFirstAudioStream, nullptr, request.ptr);
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaType> actual;
    hr = reader.ptr->GetCurrentMediaType(kFirstAudioStream, actual.Put());
    if (FAILED(hr)) return hr;
    channels = 0;
    sample_rate = 0;
    actual.ptr->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels);
    actual.ptr->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &sample_rate);
    if (channels == 0 || channels > 8 || sample_rate < 8000 || sample_rate > 192000) return MF_E_INVALIDMEDIATYPE;

    for (;;) {
        DWORD flags = 0;
        ComPtr<IMFSample> sample;
        hr = reader.ptr->ReadSample(kFirstAudioStream, 0, nullptr, &flags, nullptr, sample.Put());
        if (FAILED(hr)) return hr;
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        if (!sample.ptr) continue;
        ComPtr<IMFMediaBuffer> buffer;
        if (FAILED(sample.ptr->ConvertToContiguousBuffer(buffer.Put()))) continue;
        BYTE* data = nullptr;
        DWORD length = 0;
        if (FAILED(buffer.ptr->Lock(&data, nullptr, &length))) continue;
        if (pcm.size() + length > kMaxDecodedBytes) {
            buffer.ptr->Unlock();
            return E_OUTOFMEMORY;
        }
        pcm.insert(pcm.end(), data, data + length);
        buffer.ptr->Unlock();
    }
    return pcm.empty() ? MF_E_INVALID_FORMAT : S_OK;
}

void SoftenAndNormalize(std::vector<uint8_t>& pcm, uint32_t channels) {
    const size_t frame_bytes = static_cast<size_t>(channels) * 2;
    pcm.resize(pcm.size() - pcm.size() % frame_bytes);
    auto* samples = reinterpret_cast<int16_t*>(pcm.data());
    const size_t total = pcm.size() / 2;

    std::array<double, 8> pole1{};
    std::array<double, 8> pole2{};
    int max_after = 1;
    for (size_t i = 0; i < total; ++i) {
        const size_t ch = i % channels;
        pole1[ch] += kLowPassCoefficient * (samples[i] - pole1[ch]);
        pole2[ch] += kLowPassCoefficient * (pole1[ch] - pole2[ch]);
        const int filtered = static_cast<int>(std::lround(pole2[ch]));
        max_after = std::max(max_after, std::abs(filtered));
        samples[i] = static_cast<int16_t>(std::clamp(filtered, -32768, 32767));
    }
    const double scale = std::min(kMaxNormalizeGain, kNormalizeTarget / max_after);
    for (size_t i = 0; i < total; ++i)
        samples[i] = static_cast<int16_t>(std::clamp<long>(std::lround(samples[i] * scale), -kPcmClamp, kPcmClamp));
}

} // namespace vnotch::services::spotlightsfx

namespace sfx = vnotch::services::spotlightsfx;

extern "C" {

// Decodes, softens and normalizes the click sound. Safe to call repeatedly; later calls keep the first result.
VN_EXPORT int32_t __stdcall vn_spotlight_sfx_load(const char16_t* path, int32_t path_length, int32_t* out_hresult) noexcept {
    if (!path || path_length <= 0 || path_length >= 32768 || !out_hresult) return VN_ERR_INVALID_ARG;
    *out_hresult = 0;
    try {
        sfx::Guard guard(&sfx::g_lock);
        if (sfx::g_loaded) return VN_OK;
        InitOnceExecuteOnce(&sfx::g_mf_once, sfx::StartMediaFoundation, nullptr, nullptr);
        std::vector<uint8_t> pcm;
        uint32_t channels = 0;
        uint32_t sample_rate = 0;
        const HRESULT hr = sfx::DecodePcm(std::wstring(reinterpret_cast<const wchar_t*>(path), static_cast<size_t>(path_length)), pcm, channels, sample_rate);
        if (FAILED(hr)) {
            *out_hresult = static_cast<int32_t>(hr);
            return VN_ERR_FAIL;
        }
        sfx::SoftenAndNormalize(pcm, channels);
        const size_t sample_count = pcm.size() / sizeof(int16_t);
        sfx::g_pcm.resize(sample_count);
        std::memcpy(sfx::g_pcm.data(), pcm.data(), sample_count * sizeof(int16_t));
        sfx::g_channels = channels;
        sfx::g_sample_rate = sample_rate;
        sfx::g_loaded = true;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

// Plays the loaded sound asynchronously at volume 0..1, replacing any sound still playing.
VN_EXPORT int32_t __stdcall vn_spotlight_sfx_play(float volume) noexcept {
    if (!std::isfinite(volume)) return VN_ERR_INVALID_ARG;
    try {
        sfx::Guard guard(&sfx::g_lock);
        if (!sfx::g_loaded || sfx::g_pcm.empty()) return VN_ERR_FAIL;
        if (!sfx::g_wave_out) {
            WAVEFORMATEX fmt{};
            fmt.wFormatTag = WAVE_FORMAT_PCM;
            fmt.nChannels = static_cast<WORD>(sfx::g_channels);
            fmt.nSamplesPerSec = sfx::g_sample_rate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = static_cast<WORD>(sfx::g_channels * 2);
            fmt.nAvgBytesPerSec = sfx::g_sample_rate * fmt.nBlockAlign;
            if (waveOutOpen(&sfx::g_wave_out, WAVE_MAPPER, &fmt, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR) {
                sfx::g_wave_out = nullptr;
                return VN_ERR_FAIL;
            }
        }
        sfx::SfxSlot& slot = sfx::g_play[static_cast<size_t>(sfx::g_next_play)];
        sfx::g_next_play ^= 1;
        if (slot.prepared && !(slot.header.dwFlags & WHDR_DONE)) {
            waveOutReset(sfx::g_wave_out);
        }
        if (slot.prepared) {
            waveOutUnprepareHeader(sfx::g_wave_out, &slot.header, sizeof(WAVEHDR));
            slot.prepared = false;
        }
        const size_t count = sfx::g_pcm.size();
        slot.pcm.resize(count);
        const float gain = std::clamp(volume, 0.0f, 1.0f);
        for (size_t i = 0; i < count; ++i)
            slot.pcm[i] = static_cast<int16_t>(std::clamp<long>(std::lrintf(static_cast<float>(sfx::g_pcm[i]) * gain), -32768L, 32767L));
        slot.header = {};
        slot.header.lpData = reinterpret_cast<LPSTR>(slot.pcm.data());
        slot.header.dwBufferLength = static_cast<DWORD>(count * sizeof(int16_t));
        if (waveOutPrepareHeader(sfx::g_wave_out, &slot.header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) return VN_ERR_FAIL;
        slot.prepared = true;
        if (waveOutWrite(sfx::g_wave_out, &slot.header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) {
            waveOutUnprepareHeader(sfx::g_wave_out, &slot.header, sizeof(WAVEHDR));
            slot.prepared = false;
            return VN_ERR_FAIL;
        }
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_spotlight_sfx_stop() noexcept {
    sfx::Guard guard(&sfx::g_lock);
    if (sfx::g_wave_out) {
        waveOutReset(sfx::g_wave_out);
    }
    return VN_OK;
}

// Whether load succeeded earlier (lets the caller skip the file search).
VN_EXPORT int32_t __stdcall vn_spotlight_sfx_is_loaded(uint8_t* out_loaded) noexcept {
    if (!out_loaded) return VN_ERR_INVALID_ARG;
    sfx::Guard guard(&sfx::g_lock);
    *out_loaded = sfx::g_loaded ? 1 : 0;
    return VN_OK;
}

} // extern "C"
