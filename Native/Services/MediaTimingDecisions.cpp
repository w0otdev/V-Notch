#include "vnotch_common.h"

namespace vnotch::services
{
    class MediaTimingDecisions final
    {
    public:
        static bool IsLongHoldSource(std::u16string_view last_source) noexcept
        {
            return equals_ordinal(last_source, u"YouTube") ||
                   equals_ordinal(last_source, u"SoundCloud") ||
                   equals_ordinal(last_source, u"Browser");
        }

        static bool ShouldPreserveSoundCloud(
            std::u16string_view media_source,
            std::u16string_view cur_track,
            std::u16string_view cur_artist,
            std::u16string_view app_id,
            std::u16string_view session_key,
            std::u16string_view last_source,
            std::u16string_view last_session_key,
            int64_t last_meta_change_ticks,
            int64_t now_ticks,
            bool has_session_override,
            std::u16string_view session_override,
            double freshness_seconds) noexcept
        {
            if (!equals_ignore_case(media_source, u"Browser") || !equals_ignore_case(last_source, u"SoundCloud"))
            {
                return false;
            }
            if (is_null_or_whitespace(cur_track) ||
                is_null_or_whitespace(app_id) ||
                !is_browser_app_view(app_id) ||
                is_null_or_whitespace(session_key))
            {
                return false;
            }
            if (!equals_ordinal(last_session_key, session_key))
            {
                return false;
            }
            double elapsed_sec = static_cast<double>(now_ticks - last_meta_change_ticks) / static_cast<double>(TICKS_PER_SECOND);
            if (elapsed_sec > freshness_seconds)
            {
                return false;
            }
            if (contains_ignore_case(cur_track, u"youtube") || contains_ignore_case(cur_artist, u"youtube"))
            {
                return false;
            }
            if (has_session_override && !session_override.empty() && !equals_ignore_case(session_override, u"SoundCloud"))
            {
                return false;
            }
            return true;
        }
    };
} // namespace vnotch::services

using namespace vnotch;
using namespace vnotch::services;

VN_EXPORT int32_t vn_media_timing_is_long_hold_source(
    const char16_t* src_ptr, int32_t src_len,
    uint8_t* out_long_hold) noexcept
{
    if (out_long_hold == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_long_hold = MediaTimingDecisions::IsLongHoldSource(make_u16_view(src_ptr, src_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_timing_should_preserve_soundcloud(
    const char16_t* media_source_ptr, int32_t media_source_len,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    const char16_t* cur_artist_ptr, int32_t cur_artist_len,
    const char16_t* source_app_id_ptr, int32_t source_app_id_len,
    const char16_t* session_key_ptr, int32_t session_key_len,
    const char16_t* last_source_ptr, int32_t last_source_len,
    const char16_t* last_session_key_ptr, int32_t last_session_key_len,
    int64_t last_meta_change_ticks,
    int64_t now_ticks,
    uint8_t has_session_override,
    const char16_t* session_override_ptr, int32_t session_override_len,
    double freshness_seconds,
    uint8_t* out_preserve) noexcept
{
    if (out_preserve == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_preserve = MediaTimingDecisions::ShouldPreserveSoundCloud(
        make_u16_view(media_source_ptr, media_source_len),
        make_u16_view(cur_track_ptr, cur_track_len),
        make_u16_view(cur_artist_ptr, cur_artist_len),
        make_u16_view(source_app_id_ptr, source_app_id_len),
        make_u16_view(session_key_ptr, session_key_len),
        make_u16_view(last_source_ptr, last_source_len),
        make_u16_view(last_session_key_ptr, last_session_key_len),
        last_meta_change_ticks,
        now_ticks,
        has_session_override != 0,
        make_u16_view(session_override_ptr, session_override_len),
        freshness_seconds) ? 1u : 0u;
    return VN_OK;
}
