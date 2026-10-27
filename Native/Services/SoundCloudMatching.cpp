#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SoundCloudMatching final
    {
    public:
        static constexpr std::u16string_view SC_RESERVED_USERS[] = {
            u"search", u"charts", u"discover", u"stream", u"you", u"terms", u"privacy", u"mobile", u"upload", u"signin",
            u"login", u"settings", u"jobs", u"blog", u"developers", u"pages", u"playlists", u"stations", u"likes", u"messages",
            u"pro", u"for-artists", u"popular", u"tracks", u"explore", u"featured"
        };
        
        static constexpr std::u16string_view SC_RESERVED_SLUGS[] = {
            u"sets", u"tracks", u"likes", u"reposts", u"spotlight", u"albums", u"following", u"followers",
            u"popular-tracks", u"recommended", u"comments", u"related", u"stats", u"stations"
        };
        
        static inline bool sc_is_valid_candidate(std::u16string_view user, std::u16string_view slug) noexcept
        {
            if (is_null_or_whitespace(user) || is_null_or_whitespace(slug))
            {
                return false;
            }
            if (user.find(u'.') != std::u16string_view::npos || slug.size() < 2)
            {
                return false;
            }
            for (std::u16string_view ru : SC_RESERVED_USERS)
            {
                if (equals_ignore_case(user, ru))
                {
                    return false;
                }
            }
            for (std::u16string_view rs : SC_RESERVED_SLUGS)
            {
                if (equals_ignore_case(slug, rs))
                {
                    return false;
                }
            }
            return true;
        }
        
        static inline bool is_sc_url_stop(char16_t ch) noexcept
        {
            return ch == u'/' || ch == u'?' || ch == u'#' || is_unicode_whitespace(ch);
        }
        
        static inline bool match_sc_bracket_noise(std::u16string_view inner) noexcept
        {
            std::u16string_view t = trim_u16(inner);
            if (t.empty())
            {
                return false;
            }
            if (starts_with_ignore_case(t, u"official") && t.size() > 8 && is_unicode_whitespace(t[8]))
            {
                std::u16string_view rest = trim_start_u16(t.substr(8));
                if (equals_ignore_case(rest, u"audio") || equals_ignore_case(rest, u"video"))
                {
                    return true;
                }
                if ((starts_with_ignore_case(rest, u"music") || starts_with_ignore_case(rest, u"lyric")) &&
                    rest.size() > 5 && is_unicode_whitespace(rest[5]))
                {
                    std::u16string_view tail = trim_start_u16(rest.substr(5));
                    if (equals_ignore_case(tail, u"video"))
                    {
                        return true;
                    }
                }
            }
            if (starts_with_ignore_case(t, u"prod"))
            {
                size_t pos = 4;
                if (pos < t.size() && t[pos] == u'.')
                {
                    ++pos;
                }
                if (pos < t.size() && is_unicode_whitespace(t[pos]))
                {
                    std::u16string_view rest = trim_start_u16(t.substr(pos));
                    if (!rest.empty())
                    {
                        return true;
                    }
                }
            }
            if (starts_with_ignore_case(t, u"free") && t.size() > 4 && is_unicode_whitespace(t[4]))
            {
                std::u16string_view rest = trim_start_u16(t.substr(4));
                if (equals_ignore_case(rest, u"dl") || equals_ignore_case(rest, u"download"))
                {
                    return true;
                }
            }
            return false;
        }
        
        static inline std::u16string sc_sanitize_search_text_impl(std::u16string_view value)
        {
            if (is_null_or_whitespace(value))
            {
                return {};
            }
            std::u16string s(value);
            for (char16_t& ch : s)
            {
                if (ch == 0x2013 || ch == 0x2014)
                {
                    ch = u'-';
                }
            }
            std::u16string_view sv = trim_u16(s);
        
            std::u16string pass1;
            pass1.reserve(sv.size());
            size_t i = 0;
            while (i < sv.size())
            {
                if (sv[i] == u'(' || sv[i] == u'[')
                {
                    size_t j = i + 1;
                    while (j < sv.size() && sv[j] != u')' && sv[j] != u']')
                    {
                        ++j;
                    }
                    if (j < sv.size() && match_sc_bracket_noise(sv.substr(i + 1, j - (i + 1))))
                    {
                        pass1.push_back(u' ');
                        i = j + 1;
                        continue;
                    }
                }
                pass1.push_back(sv[i]);
                ++i;
            }
        
            for (char16_t& ch : pass1)
            {
                if (ch == u'[' || ch == u']' || ch == u'(' || ch == u')' || ch == u'"' || ch == u'\'')
                {
                    ch = u' ';
                }
            }
        
            std::u16string out;
            out.reserve(pass1.size());
            bool in_ws = false;
            for (char16_t ch : pass1)
            {
                if (is_unicode_whitespace(ch))
                {
                    if (!in_ws)
                    {
                        out.push_back(u' ');
                        in_ws = true;
                    }
                }
                else
                {
                    out.push_back(ch);
                    in_ws = false;
                }
            }
        
            std::u16string_view trimmed = trim_u16(out);
            return std::u16string(trimmed);
        }
        
        static inline bool sc_has_meaningful_artist(std::u16string_view norm_artist) noexcept
        {
            return !norm_artist.empty() &&
                   !equals_ordinal(norm_artist, u"soundcloud") &&
                   !equals_ordinal(norm_artist, u"browser") &&
                   !equals_ordinal(norm_artist, u"unknown");
        }
        
        static inline int32_t sc_score_title_match(std::u16string_view norm_title, std::u16string_view norm_slug) noexcept
        {
            if (norm_title.empty() || norm_slug.empty())
            {
                return 0;
            }
            if (equals_ordinal(norm_slug, norm_title))
            {
                return 8;
            }
            if ((norm_title.size() >= 3 && contains_ordinal(norm_slug, norm_title)) ||
                (norm_slug.size() >= 4 &&
                 (norm_slug.find(u' ') != std::u16string_view::npos || norm_slug.size() * 2 >= norm_title.size()) &&
                 contains_ordinal(norm_title, norm_slug)))
            {
                return 5;
            }
        
            int32_t score = 0;
            size_t i = 0;
            while (i < norm_title.size())
            {
                while (i < norm_title.size() && norm_title[i] == u' ')
                {
                    ++i;
                }
                if (i >= norm_title.size())
                {
                    break;
                }
                size_t start = i;
                while (i < norm_title.size() && norm_title[i] != u' ')
                {
                    ++i;
                }
                std::u16string_view token = norm_title.substr(start, i - start);
                if (token.size() >= 3 && contains_ordinal(norm_slug, token))
                {
                    score += 1;
                }
            }
            return score;
        }
        
        static inline int32_t sc_score_artist_match(std::u16string_view norm_artist, std::u16string_view norm_user) noexcept
        {
            if (!sc_has_meaningful_artist(norm_artist) || norm_user.size() < 2)
            {
                return 0;
            }
            if (equals_ordinal(norm_user, norm_artist) ||
                (norm_artist.size() >= 2 && contains_ordinal(norm_user, norm_artist)) ||
                (norm_user.size() >= 3 && contains_ordinal(norm_artist, norm_user)))
            {
                return 3;
            }
            return 0;
        }

        static int32_t vn_soundcloud_is_valid_track_candidate_impl(const char16_t* user_ptr, int32_t user_len,
    const char16_t* slug_ptr, int32_t slug_len,
    uint8_t* out_valid) noexcept
        {
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    bool ok = sc_is_valid_candidate(make_u16_view(user_ptr, user_len), make_u16_view(slug_ptr, slug_len));
    *out_valid = ok ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_soundcloud_extract_track_url_impl(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_found) noexcept
        {
    if (out_len == nullptr || out_found == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_len = 0;
    *out_found = 0u;
    std::u16string_view text = make_u16_view(text_ptr, text_len);
    if (is_null_or_whitespace(text))
    {
        return VN_OK;
    }

    for (size_t i = 0; i < text.size(); ++i)
    {
        std::u16string_view rem = text.substr(i);
        size_t host_pos = 0;
        if (starts_with_ignore_case(rem, u"https://"))
        {
            host_pos = 8;
        }
        else if (starts_with_ignore_case(rem, u"http://"))
        {
            host_pos = 7;
        }
        else
        {
            continue;
        }

        std::u16string_view after_scheme = rem.substr(host_pos);
        if (starts_with_ignore_case(after_scheme, u"www."))
        {
            after_scheme.remove_prefix(4);
        }
        else if (starts_with_ignore_case(after_scheme, u"m."))
        {
            after_scheme.remove_prefix(2);
        }

        if (!starts_with_ignore_case(after_scheme, u"soundcloud.com/"))
        {
            continue;
        }
        std::u16string_view path = after_scheme.substr(15);
        size_t u_end = 0;
        while (u_end < path.size() && !is_sc_url_stop(path[u_end]))
        {
            ++u_end;
        }
        if (u_end == 0 || u_end >= path.size() || path[u_end] != u'/')
        {
            continue;
        }
        std::u16string_view user = path.substr(0, u_end);
        std::u16string_view slug_part = path.substr(u_end + 1);
        size_t s_end = 0;
        while (s_end < slug_part.size() && !is_sc_url_stop(slug_part[s_end]))
        {
            ++s_end;
        }
        if (s_end == 0)
        {
            continue;
        }
        std::u16string_view tail = slug_part.substr(s_end);
        if (!tail.empty() && tail[0] == u'/')
        {
            tail.remove_prefix(1);
        }
        if (!tail.empty() && tail[0] != u'?' && tail[0] != u'#' && !is_unicode_whitespace(tail[0]))
        {
            continue;
        }

        std::u16string_view slug = slug_part.substr(0, s_end);
        if (!sc_is_valid_candidate(user, slug))
        {
            return VN_OK;
        }

        std::u16string url = u"https://soundcloud.com/";
        url.append(user);
        url.push_back(u'/');
        url.append(slug);
        *out_found = 1u;
        return copy_u16_out(url, out_buf, out_cap, out_len);
    }

    return VN_OK;
}

        static int32_t vn_soundcloud_sanitize_search_text_impl(const char16_t* val_ptr, int32_t val_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    if (out_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string sanitized = sc_sanitize_search_text_impl(make_u16_view(val_ptr, val_len));
    return copy_u16_out(sanitized, out_buf, out_cap, out_len);
}

        static int32_t vn_soundcloud_score_candidate_impl(const char16_t* user_ptr, int32_t user_len,
    const char16_t* slug_ptr, int32_t slug_len,
    const char16_t* norm_title_ptr, int32_t norm_title_len,
    const char16_t* norm_artist_ptr, int32_t norm_artist_len,
    int32_t* out_score) noexcept
        {
    if (out_score == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string norm_slug = normalize_for_loose_match_impl(to_lower_invariant_str(make_u16_view(slug_ptr, slug_len)));
    std::u16string norm_user = normalize_for_loose_match_impl(to_lower_invariant_str(make_u16_view(user_ptr, user_len)));
    std::u16string_view norm_title = make_u16_view(norm_title_ptr, norm_title_len);
    std::u16string_view norm_artist = make_u16_view(norm_artist_ptr, norm_artist_len);

    *out_score = sc_score_title_match(norm_title, norm_slug) + sc_score_artist_match(norm_artist, norm_user);
    return VN_OK;
}

        static int32_t vn_soundcloud_is_oembed_match_impl(const char16_t* exp_title_ptr, int32_t exp_title_len,
    const char16_t* exp_artist_ptr, int32_t exp_artist_len,
    const char16_t* cand_title_ptr, int32_t cand_title_len,
    const char16_t* cand_author_ptr, int32_t cand_author_len,
    int32_t cand_score,
    uint8_t strict_mode,
    uint8_t* out_match) noexcept
        {
    if (out_match == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_match = 0u;

    std::u16string norm_exp_title = normalize_for_loose_match_impl(sc_sanitize_search_text_impl(make_u16_view(exp_title_ptr, exp_title_len)));
    std::u16string norm_exp_artist = normalize_for_loose_match_impl(to_lower_invariant_str(make_u16_view(exp_artist_ptr, exp_artist_len)));
    std::u16string norm_cand_title = normalize_for_loose_match_impl(sc_sanitize_search_text_impl(make_u16_view(cand_title_ptr, cand_title_len)));
    std::u16string norm_cand_author = normalize_for_loose_match_impl(to_lower_invariant_str(make_u16_view(cand_author_ptr, cand_author_len)));

    if (norm_exp_title.empty() || norm_cand_title.empty())
    {
        return VN_OK;
    }

    std::u16string_view stripped_cand_title = norm_cand_title;
    if (!norm_cand_author.empty())
    {
        std::u16string suffix = u" by ";
        suffix.append(norm_cand_author);
        if (stripped_cand_title.size() > suffix.size() && ends_with_ordinal(stripped_cand_title, suffix))
        {
            stripped_cand_title = trim_end_u16(stripped_cand_title.substr(0, stripped_cand_title.size() - suffix.size()));
        }
    }

    auto extract_core = [](std::u16string_view title, std::u16string_view artist) -> std::u16string_view {
        if (sc_has_meaningful_artist(artist) && title.size() > artist.size() + 1 &&
            starts_with_ordinal(title, artist) && title[artist.size()] == u' ')
        {
            return trim_start_u16(title.substr(artist.size() + 1));
        }
        return title;
    };

    std::u16string_view core_exp = extract_core(norm_exp_title, norm_exp_artist);
    std::u16string_view core_cand = extract_core(stripped_cand_title, norm_cand_author);

    bool title_matches = (!norm_exp_title.empty() && !stripped_cand_title.empty() && equals_ordinal(norm_exp_title, stripped_cand_title)) ||
                         (!core_exp.empty() && !core_cand.empty() && equals_ordinal(core_exp, core_cand));
    if (!title_matches)
    {
        return VN_OK;
    }

    if (sc_has_meaningful_artist(norm_exp_artist))
    {
        bool artist_ok = false;
        if (!norm_cand_author.empty())
        {
            artist_ok = equals_ordinal(norm_cand_author, norm_exp_artist);
        }
        else if (norm_exp_artist.size() >= 3)
        {
            std::u16string by_artist = u" by ";
            by_artist.append(norm_exp_artist);
            bool starts = norm_cand_title.size() > norm_exp_artist.size() &&
                          starts_with_ordinal(norm_cand_title, norm_exp_artist) &&
                          norm_cand_title[norm_exp_artist.size()] == u' ';
            artist_ok = starts || ends_with_ordinal(norm_cand_title, by_artist);
        }
        *out_match = artist_ok ? 1u : 0u;
        return VN_OK;
    }

    *out_match = (strict_mode == 0 || cand_score >= 3) ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_soundcloud_normalize_artwork_url_impl(const char16_t* url_ptr, int32_t url_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    if (out_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string s(make_u16_view(url_ptr, url_len));
    replace_all_ordinal(s, u"\\u0026", u"&");
    replace_all_ordinal(s, u"\\/", u"/");
    return copy_u16_out(s, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_soundcloud_is_valid_track_candidate(const char16_t* user_ptr, int32_t user_len,
    const char16_t* slug_ptr, int32_t slug_len,
    uint8_t* out_valid) noexcept
{
    return vnotch::services::SoundCloudMatching::vn_soundcloud_is_valid_track_candidate_impl(user_ptr, user_len, slug_ptr, slug_len, out_valid);
}

VN_EXPORT int32_t vn_soundcloud_extract_track_url(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_found) noexcept
{
    try { return vnotch::services::SoundCloudMatching::vn_soundcloud_extract_track_url_impl(text_ptr, text_len, out_buf, out_cap, out_len, out_found); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_soundcloud_sanitize_search_text(const char16_t* val_ptr, int32_t val_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::SoundCloudMatching::vn_soundcloud_sanitize_search_text_impl(val_ptr, val_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_soundcloud_score_candidate(const char16_t* user_ptr, int32_t user_len,
    const char16_t* slug_ptr, int32_t slug_len,
    const char16_t* norm_title_ptr, int32_t norm_title_len,
    const char16_t* norm_artist_ptr, int32_t norm_artist_len,
    int32_t* out_score) noexcept
{
    try { return vnotch::services::SoundCloudMatching::vn_soundcloud_score_candidate_impl(user_ptr, user_len, slug_ptr, slug_len, norm_title_ptr, norm_title_len, norm_artist_ptr, norm_artist_len, out_score); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_soundcloud_is_oembed_match(const char16_t* exp_title_ptr, int32_t exp_title_len,
    const char16_t* exp_artist_ptr, int32_t exp_artist_len,
    const char16_t* cand_title_ptr, int32_t cand_title_len,
    const char16_t* cand_author_ptr, int32_t cand_author_len,
    int32_t cand_score,
    uint8_t strict_mode,
    uint8_t* out_match) noexcept
{
    try { return vnotch::services::SoundCloudMatching::vn_soundcloud_is_oembed_match_impl(exp_title_ptr, exp_title_len, exp_artist_ptr, exp_artist_len, cand_title_ptr, cand_title_len, cand_author_ptr, cand_author_len, cand_score, strict_mode, out_match); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_soundcloud_normalize_artwork_url(const char16_t* url_ptr, int32_t url_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::SoundCloudMatching::vn_soundcloud_normalize_artwork_url_impl(url_ptr, url_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_soundcloud_find_next_anchor_href(
    const char16_t* html_ptr,
    int32_t html_len,
    int32_t start_pos,
    int32_t* out_href_start,
    int32_t* out_href_len,
    int32_t* out_next_pos,
    uint8_t* out_found) noexcept
{
    if (!out_href_start || !out_href_len || !out_next_pos || !out_found) return VN_ERR_INVALID_ARG;
    *out_href_start = 0;
    *out_href_len = 0;
    *out_next_pos = html_len > 0 ? html_len : 0;
    *out_found = 0u;
    if (!html_ptr || html_len <= 0 || start_pos >= html_len) return VN_OK;

    std::u16string_view html = vnotch::make_u16_view(html_ptr, html_len);
    size_t i = static_cast<size_t>(start_pos < 0 ? 0 : start_pos);
    while (i < html.size())
    {
        if (html[i] != u'<') { ++i; continue; }
        std::u16string_view rem = html.substr(i);
        if (vnotch::starts_with_ordinal(rem, u"<!--"))
        {
            size_t end_c = rem.find(u"-->", 4);
            i = (end_c == std::u16string_view::npos) ? html.size() : (i + end_c + 3);
            continue;
        }
        if (vnotch::starts_with_ignore_case(rem, u"<script") || vnotch::starts_with_ignore_case(rem, u"<style"))
        {
            std::u16string_view close_tag = vnotch::starts_with_ignore_case(rem, u"<script") ? u"</script" : u"</style";
            i += 6;
            while (i < html.size() && !vnotch::starts_with_ignore_case(html.substr(i), close_tag)) ++i;
            while (i < html.size() && html[i] != u'>') ++i;
            if (i < html.size()) ++i;
            continue;
        }
        if (rem.size() >= 3 && (rem[1] == u'a' || rem[1] == u'A') && vnotch::is_unicode_whitespace(rem[2]))
        {
            size_t p = i + 2;
            int32_t found_start = -1;
            int32_t found_len = 0;
            while (p < html.size() && html[p] != u'>')
            {
                while (p < html.size() && (vnotch::is_unicode_whitespace(html[p]) || html[p] == u'/')) ++p;
                if (p >= html.size() || html[p] == u'>') break;
                size_t name_start = p;
                while (p < html.size() && !vnotch::is_unicode_whitespace(html[p]) && html[p] != u'=' && html[p] != u'>' && html[p] != u'/') ++p;
                std::u16string_view attr_name = html.substr(name_start, p - name_start);
                while (p < html.size() && vnotch::is_unicode_whitespace(html[p])) ++p;
                if (p < html.size() && html[p] == u'=')
                {
                    ++p;
                    while (p < html.size() && vnotch::is_unicode_whitespace(html[p])) ++p;
                    size_t val_start = p;
                    size_t val_end = p;
                    if (p < html.size() && (html[p] == u'"' || html[p] == u'\''))
                    {
                        char16_t quote = html[p++];
                        val_start = p;
                        while (p < html.size() && html[p] != quote) ++p;
                        val_end = p;
                        if (p < html.size()) ++p;
                    }
                    else
                    {
                        val_start = p;
                        while (p < html.size() && !vnotch::is_unicode_whitespace(html[p]) && html[p] != u'>') ++p;
                        val_end = p;
                    }
                    if (vnotch::equals_ignore_case(attr_name, u"href") && found_start < 0)
                    {
                        found_start = static_cast<int32_t>(val_start);
                        found_len = static_cast<int32_t>(val_end - val_start);
                    }
                }
            }
            if (p < html.size() && html[p] == u'>') ++p;
            i = p;
            if (found_start >= 0)
            {
                *out_href_start = found_start;
                *out_href_len = found_len;
                *out_next_pos = static_cast<int32_t>(i);
                *out_found = 1u;
                return VN_OK;
            }
            continue;
        }
        ++i;
    }
    *out_next_pos = static_cast<int32_t>(html.size());
    return VN_OK;
}

