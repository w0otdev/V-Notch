#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class PlatformDetector final
    {
    public:
        static int32_t vn_platform_parse_impl(const char16_t* val_ptr, int32_t val_len, int32_t* out_platform) noexcept
        {
    if (out_platform == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_platform = static_cast<int32_t>(parse_platform_view(make_u16_view(val_ptr, val_len)));
    return VN_OK;
}

        static int32_t vn_platform_to_display_string_impl(int32_t platform, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    std::u16string_view disp = platform_to_display_view(static_cast<VnMediaPlatform>(platform));
    return copy_out_u16(disp, out_buf, out_cap, out_len);
}

        static int32_t vn_platform_detect_from_app_id_impl(const char16_t* app_ptr, int32_t app_len, int32_t* out_platform) noexcept
        {
    if (out_platform == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(app_ptr, app_len);
    if (sv.empty())
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
        return VN_OK;
    }
    if (contains_ignore_case(sv, u"Spotify")) { *out_platform = static_cast<int32_t>(VnMediaPlatform::Spotify); return VN_OK; }
    if (contains_ignore_case(sv, u"Discord") || contains_ignore_case(sv, u"Vesktop")) { *out_platform = static_cast<int32_t>(VnMediaPlatform::Discord); return VN_OK; }
    if (contains_ignore_case(sv, u"Twitch")) { *out_platform = static_cast<int32_t>(VnMediaPlatform::Twitch); return VN_OK; }
    if (contains_ignore_case(sv, u"TIDAL")) { *out_platform = static_cast<int32_t>(VnMediaPlatform::Tidal); return VN_OK; }
    if (contains_ignore_case(sv, u"Deezer")) { *out_platform = static_cast<int32_t>(VnMediaPlatform::Deezer); return VN_OK; }
    *out_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
    return VN_OK;
}

        static int32_t vn_platform_is_browser_app_impl(const char16_t* app_ptr, int32_t app_len, uint8_t* out_is_browser) noexcept
        {
    if (out_is_browser == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_browser = is_browser_app_view(make_u16_view(app_ptr, app_len)) ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_platform_detect_single_window_title_impl(const char16_t* title_ptr,
    int32_t title_len,
    int32_t* out_immediate_platform,
    int32_t* out_fallback_platform) noexcept
        {
    if (out_immediate_platform == nullptr || out_fallback_platform == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string lower = to_lower_string(make_u16_view(title_ptr, title_len));
    std::u16string_view lv(lower);

    if (contains_ordinal(lv, u"youtube") && !starts_with_ordinal(lv, u"youtube -") && lv != u"youtube")
    {
        *out_immediate_platform = static_cast<int32_t>(VnMediaPlatform::YouTube);
        *out_fallback_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
        return VN_OK;
    }
    if (contains_ordinal(lv, u"twitch") && !starts_with_ordinal(lv, u"twitch -") && lv != u"twitch")
    {
        *out_immediate_platform = static_cast<int32_t>(VnMediaPlatform::Twitch);
        *out_fallback_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
        return VN_OK;
    }
    *out_immediate_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
    *out_fallback_platform = static_cast<int32_t>(match_fallback_platform_lower(lv));
    return VN_OK;
}

        static int32_t vn_platform_extract_title_from_window_impl(const char16_t* title_ptr,
    int32_t title_len,
    const char16_t* fallback_ptr,
    int32_t fallback_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
        {
    std::u16string res = extract_title_from_window_impl(
        make_u16_view(title_ptr, title_len),
        make_u16_view(fallback_ptr, fallback_len));
    return copy_out_u16(res, out_buf, out_cap, out_len);
}

        static int32_t vn_platform_parse_spotify_title_impl(const char16_t* title_ptr,
    int32_t title_len,
    char16_t* out_artist_buf,
    int32_t out_artist_cap,
    int32_t* out_artist_len,
    char16_t* out_track_buf,
    int32_t out_track_cap,
    int32_t* out_track_len) noexcept
        {
    std::u16string_view title = make_u16_view(title_ptr, title_len);
    int32_t first_sep = index_of_ordinal(title, u" - ");
    if (first_sep > 0)
    {
        std::u16string_view artist = trim_view(title.substr(0, static_cast<size_t>(first_sep)));
        std::u16string_view track = trim_view(title.substr(static_cast<size_t>(first_sep + 3)));
        if (!track.empty())
        {
            int32_t s1 = copy_out_u16(artist, out_artist_buf, out_artist_cap, out_artist_len);
            int32_t s2 = copy_out_u16(track, out_track_buf, out_track_cap, out_track_len);
            return s1 != VN_OK ? s1 : s2;
        }
    }
    int32_t s1 = copy_out_u16(u"Spotify", out_artist_buf, out_artist_cap, out_artist_len);
    int32_t s2 = copy_out_u16(trim_view(title), out_track_buf, out_track_cap, out_track_len);
    return s1 != VN_OK ? s1 : s2;
}

        static int32_t vn_platform_parse_soundcloud_title_impl(const char16_t* title_ptr,
    int32_t title_len,
    char16_t* out_artist_buf,
    int32_t out_artist_cap,
    int32_t* out_artist_len,
    char16_t* out_track_buf,
    int32_t out_track_cap,
    int32_t* out_track_len) noexcept
        {
    std::u16string_view extracted = make_u16_view(title_ptr, title_len);
    if (is_null_or_whitespace(extracted))
    {
        int32_t s1 = copy_out_u16(u"SoundCloud", out_artist_buf, out_artist_cap, out_artist_len);
        int32_t s2 = copy_out_u16(u"SoundCloud", out_track_buf, out_track_cap, out_track_len);
        return s1 != VN_OK ? s1 : s2;
    }

    std::u16string_view cleaned = trim_view(extracted);
    if (starts_with_ignore_case(cleaned, u"Stream ") && cleaned.size() > 7)
    {
        cleaned = trim_view(cleaned.substr(7));
    }

    std::u16string_view by_artist{};
    bool has_by_artist = false;
    int32_t by_idx = last_index_of_ignore_case(cleaned, u" by ");
    if (by_idx > 0 && static_cast<size_t>(by_idx + 4) < cleaned.size())
    {
        std::u16string_view cand_artist = trim_view(cleaned.substr(static_cast<size_t>(by_idx + 4)));
        std::u16string_view cand_track = trim_view(cleaned.substr(0, static_cast<size_t>(by_idx)));
        if (ends_with_ordinal(cand_track, u" -") ||
            ends_with_ordinal(cand_track, u" \u2013") ||
            ends_with_ordinal(cand_track, u" \u2014"))
        {
            std::u16string_view track = trim_end_view(cand_track.substr(0, cand_track.size() - 1));
            if (!track.empty() && !cand_artist.empty())
            {
                std::u16string_view disp_artist = trim_view(cleaned.substr(static_cast<size_t>(by_idx + 1)));
                int32_t s1 = copy_out_u16(disp_artist, out_artist_buf, out_artist_cap, out_artist_len);
                int32_t s2 = copy_out_u16(track, out_track_buf, out_track_cap, out_track_len);
                return s1 != VN_OK ? s1 : s2;
            }
        }
        if (!cand_track.empty() && !cand_artist.empty())
        {
            by_artist = cand_artist;
            has_by_artist = true;
            cleaned = cand_track;
        }
    }

    constexpr std::u16string_view DASH_SEPS[] = { u" - ", u" \u2013 ", u" \u2014 " };
    for (std::u16string_view sep : DASH_SEPS)
    {
        int32_t sep_idx = index_of_ordinal(cleaned, sep);
        if (sep_idx > 0 && static_cast<size_t>(sep_idx) + sep.size() < cleaned.size())
        {
            std::u16string_view dash_artist = trim_view(cleaned.substr(0, static_cast<size_t>(sep_idx)));
            std::u16string_view dash_track = trim_view(cleaned.substr(static_cast<size_t>(sep_idx) + sep.size()));
            if (!dash_artist.empty() && !dash_track.empty())
            {
                int32_t s1 = copy_out_u16(dash_artist, out_artist_buf, out_artist_cap, out_artist_len);
                int32_t s2 = copy_out_u16(dash_track, out_track_buf, out_track_cap, out_track_len);
                return s1 != VN_OK ? s1 : s2;
            }
        }
    }

    if (has_by_artist && !by_artist.empty())
    {
        int32_t s1 = copy_out_u16(by_artist, out_artist_buf, out_artist_cap, out_artist_len);
        int32_t s2 = copy_out_u16(cleaned, out_track_buf, out_track_cap, out_track_len);
        return s1 != VN_OK ? s1 : s2;
    }

    int32_t s1 = copy_out_u16(u"SoundCloud", out_artist_buf, out_artist_cap, out_artist_len);
    int32_t s2 = copy_out_u16(cleaned, out_track_buf, out_track_cap, out_track_len);
    return s1 != VN_OK ? s1 : s2;
}

        static int32_t vn_platform_normalize_for_loose_match_impl(const char16_t* val_ptr,
    int32_t val_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
        {
    std::u16string res = normalize_for_loose_match_impl(make_u16_view(val_ptr, val_len));
    return copy_out_u16(res, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_platform_parse(const char16_t* val_ptr, int32_t val_len, int32_t* out_platform) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_parse_impl(val_ptr, val_len, out_platform);
}

VN_EXPORT int32_t vn_platform_to_display_string(int32_t platform, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_to_display_string_impl(platform, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_platform_detect_from_app_id(const char16_t* app_ptr, int32_t app_len, int32_t* out_platform) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_detect_from_app_id_impl(app_ptr, app_len, out_platform);
}

VN_EXPORT int32_t vn_platform_is_browser_app(const char16_t* app_ptr, int32_t app_len, uint8_t* out_is_browser) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_is_browser_app_impl(app_ptr, app_len, out_is_browser);
}

VN_EXPORT int32_t vn_platform_detect_single_window_title(const char16_t* title_ptr,
    int32_t title_len,
    int32_t* out_immediate_platform,
    int32_t* out_fallback_platform) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_detect_single_window_title_impl(title_ptr, title_len, out_immediate_platform, out_fallback_platform);
}

VN_EXPORT int32_t vn_platform_extract_title_from_window(const char16_t* title_ptr,
    int32_t title_len,
    const char16_t* fallback_ptr,
    int32_t fallback_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_extract_title_from_window_impl(title_ptr, title_len, fallback_ptr, fallback_len, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_platform_parse_spotify_title(const char16_t* title_ptr,
    int32_t title_len,
    char16_t* out_artist_buf,
    int32_t out_artist_cap,
    int32_t* out_artist_len,
    char16_t* out_track_buf,
    int32_t out_track_cap,
    int32_t* out_track_len) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_parse_spotify_title_impl(title_ptr, title_len, out_artist_buf, out_artist_cap, out_artist_len, out_track_buf, out_track_cap, out_track_len);
}

VN_EXPORT int32_t vn_platform_parse_soundcloud_title(const char16_t* title_ptr,
    int32_t title_len,
    char16_t* out_artist_buf,
    int32_t out_artist_cap,
    int32_t* out_artist_len,
    char16_t* out_track_buf,
    int32_t out_track_cap,
    int32_t* out_track_len) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_parse_soundcloud_title_impl(title_ptr, title_len, out_artist_buf, out_artist_cap, out_artist_len, out_track_buf, out_track_cap, out_track_len);
}

VN_EXPORT int32_t vn_platform_normalize_for_loose_match(const char16_t* val_ptr,
    int32_t val_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::PlatformDetector::vn_platform_normalize_for_loose_match_impl(val_ptr, val_len, out_buf, out_cap, out_len);
}

