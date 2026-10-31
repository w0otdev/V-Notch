#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class MediaHeuristics final
    {
    public:
        static int32_t vn_media_heuristics_build_track_identity_impl(const char16_t* track_ptr,
    int32_t track_len,
    const char16_t* artist_ptr,
    int32_t artist_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len)
        {
    std::u16string id = build_track_identity_impl(
        make_u16_view(track_ptr, track_len),
        make_u16_view(artist_ptr, artist_len));
    return copy_out_u16(id, out_buf, out_cap, out_len);
}

        static int32_t vn_media_heuristics_build_source_override_key_impl(const char16_t* session_key_ptr,
    int32_t session_key_len,
    const char16_t* app_id_ptr,
    int32_t app_id_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len)
        {
    std::u16string_view session_key = make_u16_view(session_key_ptr, session_key_len);
    if (!is_null_or_whitespace(session_key))
    {
        return copy_out_u16(session_key, out_buf, out_cap, out_len);
    }
    std::u16string_view app_id = make_u16_view(app_id_ptr, app_id_len);
    if (is_browser_app_view(app_id))
    {
        return copy_out_u16({}, out_buf, out_cap, out_len);
    }
    return copy_out_u16(app_id, out_buf, out_cap, out_len);
}

        static int32_t vn_media_heuristics_is_track_compatible_with_window_title_impl(const char16_t* track_ptr,
    int32_t track_len,
    const char16_t* title_ptr,
    int32_t title_len,
    uint8_t* out_compatible)
        {
    if (out_compatible == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view track = make_u16_view(track_ptr, track_len);
    std::u16string_view title = make_u16_view(title_ptr, title_len);
    if (is_null_or_whitespace(track) || is_null_or_whitespace(title))
    {
        *out_compatible = 0u;
        return VN_OK;
    }
    std::u16string norm_track = normalize_for_loose_match_impl(track);
    std::u16string norm_title = normalize_for_loose_match_impl(title);
    bool ok = !norm_track.empty() && contains_ordinal(norm_title, norm_track);
    *out_compatible = ok ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_media_heuristics_is_ignored_source_app_impl(const char16_t* app_id_ptr,
    int32_t app_id_len,
    uint8_t* out_ignored)
        {
    if (out_ignored == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(app_id_ptr, app_id_len);
    if (is_null_or_whitespace(sv))
    {
        *out_ignored = 0u;
        return VN_OK;
    }
    bool ignored = contains_ignore_case(sv, u"V-Notch") || contains_ignore_case(sv, u"vnotch");
    *out_ignored = ignored ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_media_heuristics_is_likely_soundcloud_placeholder_url_impl(const char16_t* url_ptr,
    int32_t url_len,
    uint8_t* out_is_placeholder)
        {
    if (out_is_placeholder == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(url_ptr, url_len);
    if (is_null_or_whitespace(sv))
    {
        *out_is_placeholder = 1u;
        return VN_OK;
    }
    std::u16string norm(sv);
    replace_all_ordinal(norm, u"\\u0026", u"&");
    replace_all_ordinal(norm, u"\\/", u"/");
    norm = to_lower_string(norm);

    bool ph = contains_ordinal(norm, u"default_avatar") ||
              contains_ordinal(norm, u"/images/default_") ||
              contains_ordinal(norm, u"default-soundcloud") ||
              contains_ordinal(norm, u"/avatars-");
    *out_is_placeholder = ph ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_media_heuristics_normalize_track_for_comparison_impl(const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len)
        {
    std::u16string res = normalize_track_for_comparison_impl(make_u16_view(text_ptr, text_len));
    return copy_out_u16(res, out_buf, out_cap, out_len);
}

        static int32_t vn_media_heuristics_extract_core_track_name_impl(const char16_t* track_ptr,
    int32_t track_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len)
        {
    std::u16string res = extract_core_track_name_impl(make_u16_view(track_ptr, track_len));
    return copy_out_u16(res, out_buf, out_cap, out_len);
}

        static int32_t vn_media_heuristics_spotify_title_contains_track_impl(const char16_t* title_ptr,
    int32_t title_len,
    const char16_t* track_ptr,
    int32_t track_len,
    uint8_t* out_contains)
        {
    if (out_contains == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view title = make_u16_view(title_ptr, title_len);
    std::u16string_view track = make_u16_view(track_ptr, track_len);
    if (title.empty() || track.empty())
    {
        *out_contains = 0u;
        return VN_OK;
    }
    if (contains_ignore_case(title, track))
    {
        *out_contains = 1u;
        return VN_OK;
    }
    std::u16string norm_track = normalize_track_for_comparison_impl(track);
    std::u16string norm_title = normalize_track_for_comparison_impl(title);
    if (contains_ignore_case(norm_title, norm_track))
    {
        *out_contains = 1u;
        return VN_OK;
    }
    std::u16string core = extract_core_track_name_impl(track);
    if (core.size() >= 3 && contains_ignore_case(title, core))
    {
        *out_contains = 1u;
        return VN_OK;
    }
    *out_contains = 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_media_heuristics_build_track_identity(const char16_t* track_ptr,
    int32_t track_len,
    const char16_t* artist_ptr,
    int32_t artist_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_build_track_identity_impl(track_ptr, track_len, artist_ptr, artist_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_build_source_override_key(const char16_t* session_key_ptr,
    int32_t session_key_len,
    const char16_t* app_id_ptr,
    int32_t app_id_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_build_source_override_key_impl(session_key_ptr, session_key_len, app_id_ptr, app_id_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_is_track_compatible_with_window_title(const char16_t* track_ptr,
    int32_t track_len,
    const char16_t* title_ptr,
    int32_t title_len,
    uint8_t* out_compatible) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_is_track_compatible_with_window_title_impl(track_ptr, track_len, title_ptr, title_len, out_compatible);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_is_ignored_source_app(const char16_t* app_id_ptr,
    int32_t app_id_len,
    uint8_t* out_ignored) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_is_ignored_source_app_impl(app_id_ptr, app_id_len, out_ignored);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_is_likely_soundcloud_placeholder_url(const char16_t* url_ptr,
    int32_t url_len,
    uint8_t* out_is_placeholder) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_is_likely_soundcloud_placeholder_url_impl(url_ptr, url_len, out_is_placeholder);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_normalize_track_for_comparison(const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_normalize_track_for_comparison_impl(text_ptr, text_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_extract_core_track_name(const char16_t* track_ptr,
    int32_t track_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_extract_core_track_name_impl(track_ptr, track_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_media_heuristics_spotify_title_contains_track(const char16_t* title_ptr,
    int32_t title_len,
    const char16_t* track_ptr,
    int32_t track_len,
    uint8_t* out_contains) noexcept
{
    try { return vnotch::services::MediaHeuristics::vn_media_heuristics_spotify_title_contains_track_impl(title_ptr, title_len, track_ptr, track_len, out_contains);
 } catch (...) { return VN_ERR_FAIL; }
}

