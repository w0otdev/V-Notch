#include "vnotch_common.h"
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class AudioMixerService final {
public:
    AudioMixerService() = delete;

    [[nodiscard]] static constexpr bool ExtractEndpointGuid(
        std::u16string_view device_id,
        int32_t& out_start,
        int32_t& out_length) noexcept {
        out_start = 0;
        out_length = 0;

        int32_t close_idx = -1;
        int32_t open_idx = -1;
        for (int32_t i = static_cast<int32_t>(device_id.size()) - 1; i >= 0; --i) {
            if (close_idx < 0 && device_id[static_cast<size_t>(i)] == u'}') {
                close_idx = i;
            }
            if (open_idx < 0 && device_id[static_cast<size_t>(i)] == u'{') {
                open_idx = i;
            }
            if (close_idx >= 0 && open_idx >= 0) {
                break;
            }
        }

        if (open_idx >= 0 && close_idx > open_idx) {
            out_start = open_idx;
            out_length = close_idx - open_idx + 1;
            return true;
        }
        return false;
    }

    // 0 = Off, 1 = WindowsSonic, 2 = DolbyAtmos
    [[nodiscard]] static constexpr int32_t ClassifySpatialAudioValue(std::u16string_view value) noexcept {
        const auto trimmed = vn_trim(value);
        if (trimmed.empty()) {
            return 0;
        }
        if (vn_contains_icase_ascii(trimmed, u"a44d5fd2")) {
            return 1;
        }
        if (vn_contains_icase_ascii(trimmed, u"265d4a5e") || vn_contains_icase_ascii(trimmed, u"dolby")) {
            return 2;
        }
        return 0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_audio_mixer_extract_endpoint_guid(
    const char16_t* device_id,
    int32_t device_id_len,
    int32_t* out_start,
    int32_t* out_length,
    uint8_t* out_found) noexcept {
    if (!out_start || !out_length || !out_found || (!device_id && device_id_len > 0) || device_id_len < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view sv(device_id ? device_id : u"", static_cast<size_t>(device_id_len));
    int32_t start = 0;
    int32_t length = 0;
    const bool found = vnotch::services::AudioMixerService::ExtractEndpointGuid(sv, start, length);
    *out_start = start;
    *out_length = length;
    *out_found = found ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_audio_mixer_classify_spatial_audio_value(
    const char16_t* value,
    int32_t value_len,
    int32_t* out_mode) noexcept {
    if (!out_mode || (!value && value_len > 0) || value_len < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view sv(value ? value : u"", static_cast<size_t>(value_len));
    *out_mode = vnotch::services::AudioMixerService::ClassifySpatialAudioValue(sv);
    return VN_OK;
}

} // extern "C"
