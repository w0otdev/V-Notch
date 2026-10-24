#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class MediaUpdateQueue final {
public:
    static bool SameTrack(
        std::u16string_view left_track,
        std::u16string_view right_track,
        std::u16string_view left_artist,
        std::u16string_view right_artist,
        std::u16string_view left_source,
        std::u16string_view right_source,
        std::u16string_view left_session_key,
        std::u16string_view right_session_key) noexcept {
        if (!vnotch::equals_ignore_case(left_track, right_track)) {
            return false;
        }
        if (!left_session_key.empty() && !right_session_key.empty() && left_session_key != right_session_key) {
            return false;
        }
        auto is_generic_source_or_artist = [](std::u16string_view sv) noexcept {
            return sv.empty() ||
                   vnotch::equals_icase_ascii(sv, u"Browser") ||
                   vnotch::equals_icase_ascii(sv, u"YouTube") ||
                   vnotch::equals_icase_ascii(sv, u"SoundCloud") ||
                   vnotch::equals_icase_ascii(sv, u"Unknown Artist");
        };
        const bool artist_match = vnotch::equals_ignore_case(left_artist, right_artist) ||
                                  is_generic_source_or_artist(left_artist) ||
                                  is_generic_source_or_artist(right_artist);
        const bool source_match = vnotch::equals_ignore_case(left_source, right_source) ||
                                  vnotch::equals_icase_ascii(left_source, u"Browser") ||
                                  vnotch::equals_icase_ascii(right_source, u"Browser") ||
                                  left_source.empty() ||
                                  right_source.empty();
        return artist_match && source_match;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_media_update_queue_same_track(
    const char16_t* left_track, int32_t left_track_len,
    const char16_t* right_track, int32_t right_track_len,
    const char16_t* left_artist, int32_t left_artist_len,
    const char16_t* right_artist, int32_t right_artist_len,
    const char16_t* left_source, int32_t left_source_len,
    const char16_t* right_source, int32_t right_source_len,
    const char16_t* left_session_key, int32_t left_session_len,
    const char16_t* right_session_key, int32_t right_session_len,
    uint8_t* out_same) noexcept {
    if (!out_same || left_track_len < 0 || right_track_len < 0 ||
        left_artist_len < 0 || right_artist_len < 0 ||
        left_source_len < 0 || right_source_len < 0 ||
        left_session_len < 0 || right_session_len < 0) {
        return VN_E_INVALID_ARG;
    }
    *out_same = vnotch::services::MediaUpdateQueue::SameTrack(
        std::u16string_view(left_track ? left_track : u"", static_cast<size_t>(left_track_len)),
        std::u16string_view(right_track ? right_track : u"", static_cast<size_t>(right_track_len)),
        std::u16string_view(left_artist ? left_artist : u"", static_cast<size_t>(left_artist_len)),
        std::u16string_view(right_artist ? right_artist : u"", static_cast<size_t>(right_artist_len)),
        std::u16string_view(left_source ? left_source : u"", static_cast<size_t>(left_source_len)),
        std::u16string_view(right_source ? right_source : u"", static_cast<size_t>(right_source_len)),
        std::u16string_view(left_session_key ? left_session_key : u"", static_cast<size_t>(left_session_len)),
        std::u16string_view(right_session_key ? right_session_key : u"", static_cast<size_t>(right_session_len))) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
