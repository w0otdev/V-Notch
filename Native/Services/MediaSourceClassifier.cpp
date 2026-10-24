#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class MediaSourceClassifier final
    {
    public:
        static inline bool matches_metadata_token(
            std::u16string_view title,
            std::u16string_view artist,
            std::u16string_view album,
            std::u16string_view token) noexcept
        {
            if (contains_ordinal(artist, token) || contains_ordinal(album, token))
            {
                return true;
            }
            if (equals_ordinal(token, u"soundcloud"))
            {
                return equals_ordinal(title, token) ||
                       ends_with_ordinal(title, u"- soundcloud") ||
                       ends_with_ordinal(title, u"\u2013 soundcloud") ||
                       contains_ordinal(title, u"on soundcloud") ||
                       contains_ordinal(title, u"| soundcloud");
            }
            return contains_ordinal(title, token);
        }

        static int32_t vn_media_source_classify_app_id_impl(const char16_t* app_ptr,
    int32_t app_len,
    int32_t* out_platform,
    uint8_t* out_is_spotify_playing,
    uint8_t* out_set_running) noexcept
        {
    if (out_platform == nullptr || out_is_spotify_playing == nullptr || out_set_running == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
    *out_is_spotify_playing = 0u;
    *out_set_running = 0u;

    std::u16string_view app = make_u16_view(app_ptr, app_len);
    if (app.empty())
    {
        return VN_OK;
    }

    if (contains_ignore_case(app, u"Spotify"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Spotify);
        *out_is_spotify_playing = 1u;
        *out_set_running = 1u;
    }
    else if (contains_ignore_case(app, u"YouTube"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::YouTube);
        *out_set_running = 1u;
    }
    else if (contains_ignore_case(app, u"Discord") || contains_ignore_case(app, u"Vesktop"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Discord);
        *out_set_running = 1u;
    }
    else if (contains_ignore_case(app, u"Twitch"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Twitch);
        *out_set_running = 1u;
    }
    else if (contains_ignore_case(app, u"TIDAL"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Tidal);
        *out_set_running = 1u;
    }
    else if (contains_ignore_case(app, u"Deezer"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Deezer);
        *out_set_running = 1u;
    }
    else if (is_browser_app_view(app))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Browser);
        *out_set_running = 0u;
    }
    else if (contains_ignore_case(app, u"Music") ||
             contains_ignore_case(app, u"Apple") ||
             contains_ignore_case(app, u"AppleMusic"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::AppleMusic);
        *out_set_running = 1u;
    }
    else
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Browser);
        *out_set_running = 0u;
    }

    return VN_OK;
}

        static int32_t vn_media_source_detect_from_metadata_impl(const char16_t* title_ptr, int32_t title_len,
    const char16_t* artist_ptr, int32_t artist_len,
    const char16_t* album_ptr, int32_t album_len,
    int32_t* out_platform) noexcept
        {
    if (out_platform == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string title = to_lower_invariant_str(make_u16_view(title_ptr, title_len));
    std::u16string artist = to_lower_invariant_str(make_u16_view(artist_ptr, artist_len));
    std::u16string album = to_lower_invariant_str(make_u16_view(album_ptr, album_len));

    if (matches_metadata_token(title, artist, album, u"youtube") ||
        ends_with_ordinal(title, u"- youtube") ||
        ends_with_ordinal(title, u"\u2013 youtube"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::YouTube);
        return VN_OK;
    }

    if (matches_metadata_token(title, artist, album, u"twitch") ||
        ends_with_ordinal(title, u"- twitch") ||
        ends_with_ordinal(title, u"\u2013 twitch"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Twitch);
        return VN_OK;
    }

    if (matches_metadata_token(title, artist, album, u"discord"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Discord);
        return VN_OK;
    }

    if (matches_metadata_token(title, artist, album, u"apple music") ||
        contains_ordinal(album, u"music.apple.com"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::AppleMusic);
        return VN_OK;
    }

    if (contains_ordinal(artist, u"bilibili") || contains_ordinal(title, u"bilibili") ||
        contains_ordinal(artist, u"\u54d4\u54e9\u54d4\u54e9") || contains_ordinal(title, u"\u54d4\u54e9\u54d4\u54e9"))
    {
        *out_platform = static_cast<int32_t>(VnMediaPlatform::Bilibili);
        return VN_OK;
    }

    struct SimpleTokenRule
    {
        std::u16string_view token;
        VnMediaPlatform platform;
    };
    static constexpr SimpleTokenRule SIMPLE_METADATA_TOKENS[] = {
        { u"soundcloud", VnMediaPlatform::SoundCloud },
        { u"tidal", VnMediaPlatform::Tidal },
        { u"deezer", VnMediaPlatform::Deezer },
        { u"bandcamp", VnMediaPlatform::Bandcamp },
        { u"netflix", VnMediaPlatform::Netflix },
        { u"vimeo", VnMediaPlatform::Vimeo },
    };

    for (const auto& rule : SIMPLE_METADATA_TOKENS)
    {
        if (matches_metadata_token(title, artist, album, rule.token))
        {
            *out_platform = static_cast<int32_t>(rule.platform);
            return VN_OK;
        }
    }

    *out_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
    return VN_OK;
}

        static int32_t vn_media_source_detect_from_window_title_impl(const char16_t* win_title_ptr, int32_t win_title_len,
    const char16_t* track_lower_ptr, int32_t track_lower_len,
    const char16_t* track_norm_ptr, int32_t track_norm_len,
    uint8_t has_track,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    int32_t* out_platform,
    uint8_t* out_matched,
    char16_t* out_better_track_buf, int32_t out_better_track_cap, int32_t* out_better_track_len) noexcept
        {
    if (out_platform == nullptr || out_matched == nullptr || out_better_track_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_platform = static_cast<int32_t>(VnMediaPlatform::Unknown);
    *out_matched = 0u;
    *out_better_track_len = 0;

    std::u16string_view title = make_u16_view(win_title_ptr, win_title_len);
    std::u16string win_lower = to_lower_invariant_str(title);
    std::u16string_view track_lower = make_u16_view(track_lower_ptr, track_lower_len);
    std::u16string_view track_norm = make_u16_view(track_norm_ptr, track_norm_len);

    bool track_match = contains_ordinal(win_lower, track_lower);
    if (!track_match && !track_norm.empty())
    {
        std::u16string win_norm = normalize_for_loose_match_impl(win_lower);
        track_match = contains_ordinal(win_norm, track_norm);
    }

    if (has_track != 0 && !track_match)
    {
        return VN_OK;
    }

    VnMediaPlatform detected = VnMediaPlatform::Unknown;
    std::u16string_view primary_key{};

    if (contains_ordinal(win_lower, u"youtube") && !starts_with_ordinal(win_lower, u"youtube -") && !equals_ordinal(win_lower, u"youtube"))
    {
        detected = VnMediaPlatform::YouTube;
        primary_key = u"YouTube";
    }
    else if (contains_ordinal(win_lower, u"twitch") && !starts_with_ordinal(win_lower, u"twitch -") && !equals_ordinal(win_lower, u"twitch"))
    {
        detected = VnMediaPlatform::Twitch;
        primary_key = u"Twitch";
    }
    else if ((contains_ordinal(win_lower, u"discord") || contains_ordinal(win_lower, u"vesktop")) &&
             !starts_with_ordinal(win_lower, u"discord -") && !equals_ordinal(win_lower, u"discord") && !equals_ordinal(win_lower, u"vesktop"))
    {
        detected = VnMediaPlatform::Discord;
        primary_key = u"Discord";
    }
    else
    {
        detected = match_fallback_platform(win_lower);
        if (detected == VnMediaPlatform::Unknown &&
            contains_ordinal(win_lower, u"apple") && contains_ordinal(win_lower, u"music"))
        {
            detected = VnMediaPlatform::AppleMusic;
        }
    }

    if (detected == VnMediaPlatform::Unknown)
    {
        return VN_OK;
    }

    *out_platform = static_cast<int32_t>(detected);
    *out_matched = 1u;

    if (!primary_key.empty())
    {
        std::u16string extracted = extract_title_from_window_impl(title, primary_key);
        std::u16string_view cur_track = make_u16_view(cur_track_ptr, cur_track_len);
        if (!is_null_or_whitespace(extracted))
        {
            bool is_better = cur_track.empty();
            if (!is_better && extracted.size() > cur_track.size())
            {
                std::u16string norm_ext = normalize_for_loose_match_impl(extracted);
                std::u16string norm_cur = normalize_for_loose_match_impl(cur_track);
                is_better = contains_ordinal(norm_ext, norm_cur);
            }
            if (is_better)
            {
                return copy_u16_out(extracted, out_better_track_buf, out_better_track_cap, out_better_track_len);
            }
        }
    }

    return VN_OK;
}

        static int32_t vn_media_source_is_junk_title_impl(const char16_t* title_ptr, int32_t title_len,
    const char16_t* artist_ptr, int32_t artist_len,
    uint8_t* out_is_junk) noexcept
        {
    if (out_is_junk == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view raw_title = make_u16_view(title_ptr, title_len);
    std::u16string_view raw_artist = make_u16_view(artist_ptr, artist_len);
    if (raw_title.empty())
    {
        *out_is_junk = 1u;
        return VN_OK;
    }

    std::u16string lower_title = to_lower_invariant_str(raw_title);
    std::u16string lower_artist = to_lower_invariant_str(raw_artist);

    static constexpr std::u16string_view JUNK_TITLES[] = {
        u"spotify",
        u"advertisement",
        u"windows media player",
        u"spotify free",
        u"spotify premium",
        u"chrome",
        u"edge",
        u"brave",
        u"opera",
        u"firefox",
        u"discord",
        u"vesktop",
        u"twitch",
        u"netflix",
        u"tidal",
        u"deezer",
        u"bandcamp",
    };

    for (std::u16string_view jt : JUNK_TITLES)
    {
        if (equals_ordinal(lower_title, jt))
        {
            *out_is_junk = 1u;
            return VN_OK;
        }
    }

    if (equals_ordinal(lower_title, u"youtube") &&
        (raw_artist.empty() || equals_ordinal(lower_artist, u"youtube")))
    {
        *out_is_junk = 1u;
        return VN_OK;
    }

    *out_is_junk = 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_media_source_classify_app_id(const char16_t* app_ptr,
    int32_t app_len,
    int32_t* out_platform,
    uint8_t* out_is_spotify_playing,
    uint8_t* out_set_running) noexcept
{
    return vnotch::services::MediaSourceClassifier::vn_media_source_classify_app_id_impl(app_ptr, app_len, out_platform, out_is_spotify_playing, out_set_running);
}

VN_EXPORT int32_t vn_media_source_detect_from_metadata(const char16_t* title_ptr, int32_t title_len,
    const char16_t* artist_ptr, int32_t artist_len,
    const char16_t* album_ptr, int32_t album_len,
    int32_t* out_platform) noexcept
{
    return vnotch::services::MediaSourceClassifier::vn_media_source_detect_from_metadata_impl(title_ptr, title_len, artist_ptr, artist_len, album_ptr, album_len, out_platform);
}

VN_EXPORT int32_t vn_media_source_detect_from_window_title(const char16_t* win_title_ptr, int32_t win_title_len,
    const char16_t* track_lower_ptr, int32_t track_lower_len,
    const char16_t* track_norm_ptr, int32_t track_norm_len,
    uint8_t has_track,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    int32_t* out_platform,
    uint8_t* out_matched,
    char16_t* out_better_track_buf, int32_t out_better_track_cap, int32_t* out_better_track_len) noexcept
{
    return vnotch::services::MediaSourceClassifier::vn_media_source_detect_from_window_title_impl(win_title_ptr, win_title_len, track_lower_ptr, track_lower_len, track_norm_ptr, track_norm_len, has_track, cur_track_ptr, cur_track_len, out_platform, out_matched, out_better_track_buf, out_better_track_cap, out_better_track_len);
}

VN_EXPORT int32_t vn_media_source_is_junk_title(const char16_t* title_ptr, int32_t title_len,
    const char16_t* artist_ptr, int32_t artist_len,
    uint8_t* out_is_junk) noexcept
{
    return vnotch::services::MediaSourceClassifier::vn_media_source_is_junk_title_impl(title_ptr, title_len, artist_ptr, artist_len, out_is_junk);
}

