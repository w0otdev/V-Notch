#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class MediaMetadataLookupService final {
public:
    static constexpr bool IsValidYouTubeApiKeyFormat(std::u16string_view key) noexcept {
        std::u16string_view trimmed = vnotch::vn_trim(key);
        if (trimmed.empty() || trimmed.size() > 4096) return false;
        for (char16_t c : trimmed) {
            if (c < u'!' || c > u'~') return false;
        }
        return true;
    }

    static constexpr bool IsVideoIdChar(char16_t c) noexcept {
        return (c >= u'a' && c <= u'z') ||
               (c >= u'A' && c <= u'Z') ||
               (c >= u'0' && c <= u'9') ||
               c == u'_' || c == u'-';
    }

    static constexpr bool IsDirectVideoId(std::u16string_view title) noexcept {
        if (title.size() != 11) return false;
        for (char16_t c : title) {
            if (!IsVideoIdChar(c)) return false;
        }
        return true;
    }

    static constexpr bool TryExtractYouTubeInitialDataSlice(
        std::u16string_view html,
        int32_t& out_start,
        int32_t& out_len) noexcept {
        out_start = 0;
        out_len = 0;
        constexpr std::u16string_view kMarker = u"var ytInitialData = ";
        constexpr std::u16string_view kAltMarker = u"window[\"ytInitialData\"] = ";

        size_t start_idx = html.find(kMarker);
        if (start_idx == std::u16string_view::npos) {
            start_idx = html.find(kAltMarker);
            if (start_idx == std::u16string_view::npos) return false;
            start_idx += kAltMarker.size();
        } else {
            start_idx += kMarker.size();
        }

        size_t end_idx = html.find(u";</script>", start_idx);
        if (end_idx == std::u16string_view::npos) {
            end_idx = html.find(u"};\n", start_idx);
        }
        if (end_idx == std::u16string_view::npos || end_idx < start_idx) {
            return false;
        }

        while (end_idx > start_idx && html[end_idx - 1] == u';') {
            --end_idx;
        }

        out_start = static_cast<int32_t>(start_idx);
        out_len = static_cast<int32_t>(end_idx - start_idx);
        return out_len > 0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_metadata_is_valid_api_key_format(
    const char16_t* key,
    int32_t key_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid || key_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(key ? key : u"", static_cast<size_t>(key_len));
    *out_valid = vnotch::services::MediaMetadataLookupService::IsValidYouTubeApiKeyFormat(sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_metadata_is_direct_video_id(
    const char16_t* title,
    int32_t title_len,
    uint8_t* out_is_video_id) noexcept {
    if (!out_is_video_id || title_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(title ? title : u"", static_cast<size_t>(title_len));
    *out_is_video_id = vnotch::services::MediaMetadataLookupService::IsDirectVideoId(sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_metadata_extract_yt_initial_data_slice(
    const char16_t* html,
    int32_t html_len,
    int32_t* out_start,
    int32_t* out_len,
    uint8_t* out_found) noexcept {
    if (!out_start || !out_len || !out_found || html_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(html ? html : u"", static_cast<size_t>(html_len));
    *out_found = vnotch::services::MediaMetadataLookupService::TryExtractYouTubeInitialDataSlice(
        sv, *out_start, *out_len) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
