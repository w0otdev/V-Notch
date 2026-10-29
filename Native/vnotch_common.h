#pragma once

#include "vnotch_native.h"
#include "vnotch_unicode_tables.h"
#include <atomic>

namespace vnotch
{

constexpr int64_t TICKS_PER_MILLISECOND = 10000LL;
constexpr int64_t TICKS_PER_SECOND = 10000000LL;
constexpr int64_t TICKS_PER_MINUTE = 60LL * TICKS_PER_SECOND;
constexpr int64_t TICKS_PER_HOUR = 60LL * TICKS_PER_MINUTE;
constexpr int64_t TICKS_PER_DAY = 24LL * TICKS_PER_HOUR;

constexpr bool is_unicode_whitespace(char16_t ch) noexcept
{
    if (ch <= u' ')
    {
        return ch == u' ' || (ch >= u'\t' && ch <= u'\r');
    }
    return ch == 0x0085u || ch == 0x00A0u || ch == 0x1680u ||
           (ch >= 0x2000u && ch <= 0x200Au) ||
           ch == 0x2028u || ch == 0x2029u || ch == 0x202Fu ||
           ch == 0x205Fu || ch == 0x3000u;
}

inline char16_t to_lower_invariant_os(char16_t ch) noexcept
{
    wchar_t in_ch = static_cast<wchar_t>(ch);
    wchar_t out_ch = in_ch;
    if (LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, &in_ch, 1, &out_ch, 1, nullptr, nullptr, 0) == 1)
    {
        return static_cast<char16_t>(out_ch);
    }
    return ch;
}

// Non-ASCII lowercase results are cached in 256-entry pages that are filled on first use, so each BMP page
// pays at most 256 LCMapStringEx calls for the lifetime of the process instead of one call per character.
inline std::atomic<uint8_t> g_lower_page_ready[256]{};
inline char16_t g_lower_pages[256][256];
inline SRWLOCK g_lower_page_lock = SRWLOCK_INIT;

inline void fill_lower_page(uint32_t page) noexcept
{
    AcquireSRWLockExclusive(&g_lower_page_lock);
    if (g_lower_page_ready[page].load(std::memory_order_relaxed) == 0)
    {
        for (uint32_t lo = 0; lo < 256u; ++lo)
        {
            const char16_t ch = static_cast<char16_t>((page << 8) | lo);
            g_lower_pages[page][lo] = ch < 0x80u ? ch : to_lower_invariant_os(ch);
        }
        g_lower_page_ready[page].store(1, std::memory_order_release);
    }
    ReleaseSRWLockExclusive(&g_lower_page_lock);
}

inline char16_t to_lower_invariant(char16_t ch) noexcept
{
    if (ch >= u'A' && ch <= u'Z')
    {
        return static_cast<char16_t>(ch + 32);
    }
    if (ch < 0x80u)
    {
        return ch;
    }
    const uint32_t page = static_cast<uint32_t>(ch) >> 8;
    if (g_lower_page_ready[page].load(std::memory_order_acquire) == 0)
    {
        fill_lower_page(page);
    }
    return g_lower_pages[page][ch & 0xFFu];
}

inline std::u16string to_lower_string(std::u16string_view sv)
{
    std::u16string out;
    out.resize(sv.size());
    for (size_t i = 0; i < sv.size(); ++i)
    {
        out[i] = to_lower_invariant(sv[i]);
    }
    return out;
}

inline std::u16string to_lower_invariant_str(std::u16string_view sv)
{
    return to_lower_string(sv);
}

constexpr std::u16string_view trim_view(std::u16string_view sv) noexcept
{
    size_t start = 0;
    size_t end = sv.size();
    while (start < end && is_unicode_whitespace(sv[start]))
    {
        ++start;
    }
    while (end > start && is_unicode_whitespace(sv[end - 1]))
    {
        --end;
    }
    return sv.substr(start, end - start);
}

constexpr std::u16string_view trim_start_view(std::u16string_view sv) noexcept
{
    size_t start = 0;
    while (start < sv.size() && is_unicode_whitespace(sv[start]))
    {
        ++start;
    }
    return sv.substr(start);
}

constexpr std::u16string_view trim_end_view(std::u16string_view sv) noexcept
{
    size_t end = sv.size();
    while (end > 0 && is_unicode_whitespace(sv[end - 1]))
    {
        --end;
    }
    return sv.substr(0, end);
}

constexpr std::u16string_view trim_u16(std::u16string_view sv) noexcept
{
    return trim_view(sv);
}

constexpr std::u16string_view trim_start_u16(std::u16string_view sv) noexcept
{
    return trim_start_view(sv);
}

constexpr std::u16string_view trim_end_u16(std::u16string_view sv) noexcept
{
    return trim_end_view(sv);
}

constexpr bool is_null_or_whitespace(std::u16string_view sv) noexcept
{
    for (size_t i = 0; i < sv.size(); ++i)
    {
        if (!is_unicode_whitespace(sv[i]))
        {
            return false;
        }
    }
    return true;
}

constexpr bool equals_ordinal(std::u16string_view a, std::u16string_view b) noexcept
{
    return a == b;
}

inline bool equals_ignore_case(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size())
    {
        return false;
    }
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (to_lower_invariant(a[i]) != to_lower_invariant(b[i]))
        {
            return false;
        }
    }
    return true;
}

constexpr int32_t index_of_ordinal(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    if (needle.empty())
    {
        return 0;
    }
    if (needle.size() > haystack.size())
    {
        return -1;
    }
    size_t pos = haystack.find(needle);
    return pos == std::u16string_view::npos ? -1 : static_cast<int32_t>(pos);
}

inline int32_t index_of_ignore_case(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    if (needle.empty())
    {
        return 0;
    }
    if (needle.size() > haystack.size())
    {
        return -1;
    }
    // Fold the needle once (stack buffer for short needles) and reject on the first character before comparing the rest.
    constexpr size_t kFoldedCapacity = 64;
    char16_t folded[kFoldedCapacity]{};
    const bool use_folded = needle.size() <= kFoldedCapacity;
    if (use_folded)
    {
        for (size_t j = 0; j < needle.size(); ++j)
        {
            folded[j] = to_lower_invariant(needle[j]);
        }
    }
    const char16_t first = use_folded ? folded[0] : to_lower_invariant(needle[0]);
    size_t limit = haystack.size() - needle.size();
    for (size_t i = 0; i <= limit; ++i)
    {
        if (to_lower_invariant(haystack[i]) != first)
        {
            continue;
        }
        bool match = true;
        for (size_t j = 1; j < needle.size(); ++j)
        {
            const char16_t expected = use_folded ? folded[j] : to_lower_invariant(needle[j]);
            if (to_lower_invariant(haystack[i + j]) != expected)
            {
                match = false;
                break;
            }
        }
        if (match)
        {
            return static_cast<int32_t>(i);
        }
    }
    return -1;
}

inline int32_t last_index_of_ignore_case(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    if (needle.empty())
    {
        return static_cast<int32_t>(haystack.size());
    }
    if (needle.size() > haystack.size())
    {
        return -1;
    }
    constexpr size_t kFoldedCapacity = 64;
    char16_t folded[kFoldedCapacity]{};
    const bool use_folded = needle.size() <= kFoldedCapacity;
    if (use_folded)
    {
        for (size_t j = 0; j < needle.size(); ++j)
        {
            folded[j] = to_lower_invariant(needle[j]);
        }
    }
    for (size_t i = haystack.size() - needle.size() + 1; i > 0; --i)
    {
        size_t idx = i - 1;
        bool match = true;
        for (size_t j = 0; j < needle.size(); ++j)
        {
            const char16_t expected = use_folded ? folded[j] : to_lower_invariant(needle[j]);
            if (to_lower_invariant(haystack[idx + j]) != expected)
            {
                match = false;
                break;
            }
        }
        if (match)
        {
            return static_cast<int32_t>(idx);
        }
    }
    return -1;
}

constexpr bool contains_ordinal(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    return index_of_ordinal(haystack, needle) >= 0;
}

inline bool contains_ignore_case(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    return index_of_ignore_case(haystack, needle) >= 0;
}

constexpr bool starts_with_ordinal(std::u16string_view s, std::u16string_view prefix) noexcept
{
    return s.size() >= prefix.size() && s.substr(0, prefix.size()) == prefix;
}

inline bool starts_with_ignore_case(std::u16string_view s, std::u16string_view prefix) noexcept
{
    return s.size() >= prefix.size() && equals_ignore_case(s.substr(0, prefix.size()), prefix);
}

constexpr bool ends_with_ordinal(std::u16string_view s, std::u16string_view suffix) noexcept
{
    return s.size() >= suffix.size() && s.substr(s.size() - suffix.size()) == suffix;
}

inline bool ends_with_ignore_case(std::u16string_view s, std::u16string_view suffix) noexcept
{
    return s.size() >= suffix.size() && equals_ignore_case(s.substr(s.size() - suffix.size()), suffix);
}

inline std::u16string_view make_u16_view(const char16_t* ptr, int32_t len) noexcept
{
    if (ptr == nullptr || len <= 0)
    {
        return {};
    }
    return std::u16string_view(ptr, static_cast<size_t>(len));
}

// Strict variant for exports: rejects a negative length and a null pointer paired with a positive length
// instead of silently turning the bad input into an empty view. A null pointer with length 0 is an empty string.
inline bool try_make_u16_view(const char16_t* ptr, int32_t len, std::u16string_view& out) noexcept
{
    if (len < 0 || (ptr == nullptr && len > 0))
    {
        return false;
    }
    out = len == 0 ? std::u16string_view{} : std::u16string_view(ptr, static_cast<size_t>(len));
    return true;
}

inline int32_t copy_out_u16(std::u16string_view src, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (out_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    int32_t required = static_cast<int32_t>(src.size());
    *out_len = required;
    if (required == 0)
    {
        return VN_OK;
    }
    if (out_buf == nullptr || out_cap < required)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    std::memcpy(out_buf, src.data(), static_cast<size_t>(required) * sizeof(char16_t));
    return VN_OK;
}

inline int32_t copy_u16_out(std::u16string_view src, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    return copy_out_u16(src, out_buf, out_cap, out_len);
}

inline std::u16string normalize_unicode_form(std::u16string_view input, NORM_FORM form)
{
    if (input.empty())
    {
        return {};
    }
    int src_len = static_cast<int>(input.size());
    int est = NormalizeString(form, reinterpret_cast<LPCWSTR>(input.data()), src_len, nullptr, 0);
    if (est <= 0)
    {
        return std::u16string(input);
    }
    std::u16string buffer;
    buffer.resize(static_cast<size_t>(est + 8));
    int actual = NormalizeString(
        form,
        reinterpret_cast<LPCWSTR>(input.data()),
        src_len,
        reinterpret_cast<LPWSTR>(buffer.data()),
        static_cast<int>(buffer.size()));
    if (actual <= 0)
    {
        if (GetLastError() == ERROR_INSUFFICIENT_BUFFER)
        {
            int retry_size = -actual;
            if (retry_size > 0)
            {
                buffer.resize(static_cast<size_t>(retry_size + 4));
                actual = NormalizeString(
                    form,
                    reinterpret_cast<LPCWSTR>(input.data()),
                    src_len,
                    reinterpret_cast<LPWSTR>(buffer.data()),
                    static_cast<int>(buffer.size()));
            }
        }
        if (actual <= 0)
        {
            return std::u16string(input);
        }
    }
    buffer.resize(static_cast<size_t>(actual));
    return buffer;
}

enum class VnMediaPlatform : int32_t
{
    Unknown = 0,
    Spotify = 1,
    YouTube = 2,
    SoundCloud = 3,
    AppleMusic = 4,
    Browser = 5,
    Instagram = 8,
    Twitter = 9,
    Twitch = 10,
    Discord = 11,
    Netflix = 12,
    Tidal = 13,
    Deezer = 14,
    Bandcamp = 15,
    Bilibili = 16,
    Vimeo = 17
};

constexpr std::u16string_view platform_to_display_view(VnMediaPlatform p) noexcept
{
    switch (p)
    {
        case VnMediaPlatform::Spotify: return u"Spotify";
        case VnMediaPlatform::YouTube: return u"YouTube";
        case VnMediaPlatform::SoundCloud: return u"SoundCloud";
        case VnMediaPlatform::AppleMusic: return u"Apple Music";
        case VnMediaPlatform::Browser: return u"Browser";
        case VnMediaPlatform::Instagram: return u"Instagram";
        case VnMediaPlatform::Twitter: return u"Twitter";
        case VnMediaPlatform::Twitch: return u"Twitch";
        case VnMediaPlatform::Discord: return u"Discord";
        case VnMediaPlatform::Netflix: return u"Netflix";
        case VnMediaPlatform::Tidal: return u"TIDAL";
        case VnMediaPlatform::Deezer: return u"Deezer";
        case VnMediaPlatform::Bandcamp: return u"Bandcamp";
        case VnMediaPlatform::Bilibili: return u"Bilibili";
        case VnMediaPlatform::Vimeo: return u"Vimeo";
        default: return u"";
    }
}

inline VnMediaPlatform parse_platform_view(std::u16string_view val) noexcept
{
    if (val.empty())
    {
        return VnMediaPlatform::Unknown;
    }
    if (equals_ignore_case(val, u"spotify")) return VnMediaPlatform::Spotify;
    if (equals_ignore_case(val, u"youtube")) return VnMediaPlatform::YouTube;
    if (equals_ignore_case(val, u"soundcloud")) return VnMediaPlatform::SoundCloud;
    if (equals_ignore_case(val, u"apple music")) return VnMediaPlatform::AppleMusic;
    if (equals_ignore_case(val, u"browser")) return VnMediaPlatform::Browser;
    if (equals_ignore_case(val, u"instagram")) return VnMediaPlatform::Instagram;
    if (equals_ignore_case(val, u"twitter")) return VnMediaPlatform::Twitter;
    if (equals_ignore_case(val, u"twitch")) return VnMediaPlatform::Twitch;
    if (equals_ignore_case(val, u"discord") || equals_ignore_case(val, u"vesktop")) return VnMediaPlatform::Discord;
    if (equals_ignore_case(val, u"netflix")) return VnMediaPlatform::Netflix;
    if (equals_ignore_case(val, u"tidal")) return VnMediaPlatform::Tidal;
    if (equals_ignore_case(val, u"deezer")) return VnMediaPlatform::Deezer;
    if (equals_ignore_case(val, u"bandcamp")) return VnMediaPlatform::Bandcamp;
    if (equals_ignore_case(val, u"bilibili")) return VnMediaPlatform::Bilibili;
    if (equals_ignore_case(val, u"vimeo")) return VnMediaPlatform::Vimeo;
    return VnMediaPlatform::Unknown;
}

constexpr std::u16string_view BROWSER_APP_PATTERNS[] = {
    u"Chrome", u"Edge", u"Firefox", u"MS-Edge", u"msedge", u"Opera",
    u"Brave", u"Vivaldi", u"Coccoc", u"Arc", u"Sidekick", u"Zen",
    u"Thorium", u"Waterfox", u"Floorp", u"Librewolf", u"Chromium",
    u"Whale", u"Yandex", u"Wavebox", u"Helium", u"Supermium", u"Browser"
};

inline bool is_browser_app_view(std::u16string_view app_id) noexcept
{
    if (app_id.empty())
    {
        return false;
    }
    for (std::u16string_view pat : BROWSER_APP_PATTERNS)
    {
        if (contains_ignore_case(app_id, pat))
        {
            return true;
        }
    }
    return false;
}

inline std::u16string normalize_for_loose_match_impl(std::u16string_view value)
{
    if (is_null_or_whitespace(value))
    {
        return {};
    }
    std::u16string folded = normalize_unicode_form(value, NormalizationD);
    std::u16string sb;
    sb.reserve(folded.size());
    bool last_was_space = false;

    for (size_t i = 0; i < folded.size(); ++i)
    {
        char16_t ch = folded[i];
        if (vn_is_non_spacing_mark(ch))
        {
            continue;
        }
        if (vn_is_letter_or_digit(ch))
        {
            sb.push_back(to_lower_invariant(ch));
            last_was_space = false;
        }
        else if (!last_was_space)
        {
            sb.push_back(u' ');
            last_was_space = true;
        }
    }
    if (!sb.empty() && sb.back() == u' ')
    {
        sb.pop_back();
    }
    return sb;
}

inline std::u16string build_track_identity_impl(std::u16string_view track, std::u16string_view artist)
{
    std::u16string_view t = trim_view(track);
    std::u16string_view a = trim_view(artist);
    std::u16string out;
    out.reserve(t.size() + 1 + a.size());
    for (size_t i = 0; i < t.size(); ++i)
    {
        out.push_back(to_lower_invariant(t[i]));
    }
    out.push_back(u'|');
    for (size_t i = 0; i < a.size(); ++i)
    {
        out.push_back(to_lower_invariant(a[i]));
    }
    return out;
}

inline void replace_all_ordinal(std::u16string& str, std::u16string_view from, std::u16string_view to)
{
    if (from.empty())
    {
        return;
    }
    size_t pos = 0;
    while ((pos = str.find(from, pos)) != std::u16string::npos)
    {
        str.replace(pos, from.size(), to);
        pos += to.size();
    }
}

inline std::u16string normalize_track_for_comparison_impl(std::u16string_view text)
{
    std::u16string res(text);
    replace_all_ordinal(res, u" (", u" - ");
    replace_all_ordinal(res, u"(", u" - ");
    replace_all_ordinal(res, u")", u"");
    replace_all_ordinal(res, u" [", u" - ");
    replace_all_ordinal(res, u"[", u" - ");
    replace_all_ordinal(res, u"]", u"");
    std::u16string_view trimmed = trim_view(res);
    return std::u16string(trimmed);
}

inline std::u16string extract_core_track_name_impl(std::u16string_view track)
{
    size_t paren_idx = track.find(u'(');
    size_t bracket_idx = track.find(u'[');
    int32_t p = paren_idx == std::u16string_view::npos ? -1 : static_cast<int32_t>(paren_idx);
    int32_t b = bracket_idx == std::u16string_view::npos ? -1 : static_cast<int32_t>(bracket_idx);
    int32_t cut_idx = -1;
    if (p > 0 && b > 0)
        cut_idx = std::min(p, b);
    else if (p > 0)
        cut_idx = p;
    else if (b > 0)
        cut_idx = b;

    if (cut_idx > 0)
    {
        return std::u16string(trim_view(track.substr(0, static_cast<size_t>(cut_idx))));
    }
    return std::u16string(track);
}

constexpr std::u16string_view TITLE_SEPARATORS[] = {
    u" - YouTube", u" \u2013 YouTube",
    u" | Listen online for free on SoundCloud", u" | Free Listening on SoundCloud", u" | Stream on SoundCloud",
    u" on SoundCloud", u" - SoundCloud", u" \u2013 SoundCloud", u" | SoundCloud",
    u" / X", u" \u2022 Instagram",
    u" - Apple Music", u" \u2013 Apple Music",
    u" - Twitch", u" \u2013 Twitch", u" | Twitch", u" \u2022 Twitch",
    u" - Discord", u" \u2013 Discord", u" | Discord", u" \u2022 Discord",
    u" - TIDAL", u" \u2013 TIDAL", u" - Deezer", u" \u2013 Deezer",
    u" | Bandcamp", u" - Netflix", u" \u2013 Netflix",
    u" _\u54D4\u54E9\u54D4\u54E9_bilibili", u" - \u54D4\u54E9\u54D4\u54E9", u" - bilibili",
    u" on Vimeo", u" - Crunchyroll", u" \u2013 Crunchyroll",
    u" - Google Chrome", u" - Microsoft\u200B Edge", u" - Microsoft Edge",
    u" - Mozilla Firefox", u" - Opera", u" - Brave", u" - Vivaldi",
    u" - C\u1ED1c C\u1ED1c", u" - Arc", u" - Sidekick", u" - Zen Browser", u" - Zen",
    u" - Thorium", u" - Waterfox", u" - Floorp", u" - Yandex",
    u" - Browser", u" \u2013 Current browser"
};

inline std::u16string extract_title_from_window_impl(std::u16string_view window_title, std::u16string_view platform_fallback)
{
    std::u16string_view t = window_title;

    if (!t.empty() && t[0] == u'(')
    {
        size_t idx = 1;
        while (idx < t.size() && t[idx] >= u'0' && t[idx] <= u'9')
        {
            ++idx;
        }
        if (idx > 1)
        {
            if (idx < t.size() && t[idx] == u'+')
            {
                ++idx;
            }
            if (idx < t.size() && t[idx] == u')')
            {
                ++idx;
                while (idx < t.size() && is_unicode_whitespace(t[idx]))
                {
                    ++idx;
                }
                t = t.substr(idx);
            }
        }
    }

    {
        size_t idx = 0;
        while (idx < t.size() &&
               (t[idx] == 0x25B6u || t[idx] == 0x23F8u || t[idx] == 0xFE0Fu || is_unicode_whitespace(t[idx])))
        {
            ++idx;
        }
        t = t.substr(idx);
    }

    {
        size_t idx = 0;
        while (idx < t.size() &&
               (t[idx] == 0x25B6u || t[idx] == 0x23F8u || is_unicode_whitespace(t[idx]) ||
                (t[idx] >= u'0' && t[idx] <= u'9') || t[idx] == u':'))
        {
            ++idx;
        }
        if (idx > 0 && idx < t.size() && t[idx] == u'|')
        {
            t = t.substr(idx + 1);
        }
        t = trim_view(t);
    }

    for (std::u16string_view sep : TITLE_SEPARATORS)
    {
        int32_t pos = index_of_ignore_case(t, sep);
        if (pos > 0)
        {
            t = t.substr(0, static_cast<size_t>(pos));
        }
    }

    t = trim_view(t);

    {
        std::u16string_view te = trim_end_view(t);
        if (!te.empty())
        {
            char16_t last = te.back();
            if (last == u'-' || last == u'|' || last == 0x2013u || last == 0x2022u)
            {
                size_t k = te.size() - 1;
                size_t ws_end = k;
                while (k > 0 && is_unicode_whitespace(te[k - 1]))
                {
                    --k;
                }
                if (k < ws_end)
                {
                    t = te.substr(0, k);
                }
            }
        }
    }

    return t.empty() ? std::u16string(platform_fallback) : std::u16string(t);
}

inline VnMediaPlatform match_fallback_platform_lower(std::u16string_view s) noexcept
{
    if ((contains_ordinal(s, u"discord") || contains_ordinal(s, u"vesktop")) &&
        !starts_with_ordinal(s, u"discord -") && s != u"discord" && s != u"vesktop")
    {
        return VnMediaPlatform::Discord;
    }
    if (contains_ordinal(s, u"soundcloud"))
    {
        return VnMediaPlatform::SoundCloud;
    }
    if (contains_ordinal(s, u"apple music") || contains_ordinal(s, u"music.apple.com"))
    {
        return VnMediaPlatform::AppleMusic;
    }
    if (contains_ordinal(s, u"tidal") &&
        (contains_ordinal(s, u"listen.tidal.com") || contains_ordinal(s, u" - tidal") || contains_ordinal(s, u" \u2013 tidal")))
    {
        return VnMediaPlatform::Tidal;
    }
    if (contains_ordinal(s, u"deezer"))
    {
        return VnMediaPlatform::Deezer;
    }
    if (contains_ordinal(s, u"bandcamp"))
    {
        return VnMediaPlatform::Bandcamp;
    }
    if (contains_ordinal(s, u"netflix"))
    {
        return VnMediaPlatform::Netflix;
    }
    if (contains_ordinal(s, u"bilibili") || contains_ordinal(s, u"\u54D4\u54E9\u54D4\u54E9"))
    {
        return VnMediaPlatform::Bilibili;
    }
    if (contains_ordinal(s, u"vimeo"))
    {
        return VnMediaPlatform::Vimeo;
    }
    if (contains_ordinal(s, u"instagram") && (contains_ordinal(s, u"reel") || contains_ordinal(s, u"video")))
    {
        return VnMediaPlatform::Instagram;
    }
    if ((contains_ordinal(s, u"twitter") || contains_ordinal(s, u" / x")) &&
        (contains_ordinal(s, u"video") || contains_ordinal(s, u"watch")))
    {
        return VnMediaPlatform::Twitter;
    }
    return VnMediaPlatform::Unknown;
}

inline VnMediaPlatform match_fallback_platform(std::u16string_view s) noexcept
{
    return match_fallback_platform_lower(s);
}

inline std::u16string settings_search_normalize_impl(std::u16string_view input)
{
    if (is_null_or_whitespace(input))
    {
        return {};
    }
    std::u16string decomposed = normalize_unicode_form(input, NormalizationKD);
    std::u16string res;
    res.reserve(decomposed.size());
    bool prev_sep = true;

    for (size_t i = 0; i < decomposed.size(); ++i)
    {
        char16_t ch = decomposed[i];
        if (vn_is_combining_mark(ch))
        {
            continue;
        }
        char16_t norm_ch = (ch == 0x0111u || ch == 0x0110u) ? u'd' : to_lower_invariant(ch);
        if (vn_is_letter_or_digit(norm_ch))
        {
            res.push_back(norm_ch);
            prev_sep = false;
        }
        else if (!prev_sep)
        {
            res.push_back(u' ');
            prev_sep = true;
        }
    }
    if (!res.empty() && res.back() == u' ')
    {
        res.pop_back();
    }
    return res;
}

constexpr bool is_whitespace(char16_t ch) noexcept
{
    return is_unicode_whitespace(ch);
}

constexpr bool is_ascii_digit(char16_t ch) noexcept
{
    return ch >= u'0' && ch <= u'9';
}

constexpr bool is_ascii_alpha(char16_t ch) noexcept
{
    return (ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z');
}

constexpr char16_t to_upper_ascii(char16_t ch) noexcept
{
    return (ch >= u'a' && ch <= u'z') ? static_cast<char16_t>(ch - 32) : ch;
}

constexpr char16_t to_lower_ascii(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
}

constexpr bool is_ascii_hex(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') || (ch >= u'a' && ch <= u'f') || (ch >= u'A' && ch <= u'F');
}

constexpr bool equals_icase_ascii(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (to_lower_ascii(a[i]) != to_lower_ascii(b[i])) return false;
    }
    return true;
}

constexpr bool starts_with_icase_ascii(std::u16string_view s, std::u16string_view prefix) noexcept
{
    return s.size() >= prefix.size() && equals_icase_ascii(s.substr(0, prefix.size()), prefix);
}

constexpr bool ends_with_icase_ascii(std::u16string_view s, std::u16string_view suffix) noexcept
{
    return s.size() >= suffix.size() && equals_icase_ascii(s.substr(s.size() - suffix.size()), suffix);
}

constexpr bool contains_icase_ascii(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    if (needle.empty()) return true;
    if (haystack.size() < needle.size()) return false;
    const size_t limit = haystack.size() - needle.size();
    for (size_t i = 0; i <= limit; ++i)
    {
        if (equals_icase_ascii(haystack.substr(i, needle.size()), needle)) return true;
    }
    return false;
}

inline bool equals_icase_win(std::u16string_view a, std::u16string_view b) noexcept
{
    return equals_ignore_case(a, b);
}

inline bool contains_icase_win(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    return contains_ignore_case(haystack, needle);
}

constexpr std::u16string_view vn_trim(std::u16string_view sv) noexcept
{
    return trim_view(sv);
}

constexpr bool vn_is_whitespace(char16_t ch) noexcept
{
    return is_unicode_whitespace(ch);
}

constexpr bool vn_equals_icase_ascii(std::u16string_view a, std::u16string_view b) noexcept
{
    return equals_icase_ascii(a, b);
}

constexpr bool vn_starts_with_icase_ascii(std::u16string_view s, std::u16string_view prefix) noexcept
{
    return starts_with_icase_ascii(s, prefix);
}

constexpr bool vn_ends_with_icase_ascii(std::u16string_view s, std::u16string_view suffix) noexcept
{
    return ends_with_icase_ascii(s, suffix);
}

constexpr bool vn_contains_icase_ascii(std::u16string_view haystack, std::u16string_view needle) noexcept
{
    return contains_icase_ascii(haystack, needle);
}

#ifndef VN_E_INVALID_ARG
#define VN_E_INVALID_ARG VN_ERR_INVALID_ARG
#endif

constexpr std::u16string_view trim(std::u16string_view sv) noexcept
{
    return trim_view(sv);
}

inline std::u16string_view make_view(const char16_t* ptr, int32_t len) noexcept
{
    return make_u16_view(ptr, len);
}

inline int32_t write_u16_out(std::u16string_view src, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    return copy_out_u16(src, out_buf, out_cap, out_len);
}

inline bool equals_icase(std::u16string_view a, std::u16string_view b) noexcept
{
    return equals_ignore_case(a, b);
}

inline bool starts_with_icase(std::u16string_view s, std::u16string_view prefix) noexcept
{
    return starts_with_ignore_case(s, prefix);
}

inline bool is_unicode_letter_or_digit(char16_t ch) noexcept
{
    return vn_is_letter_or_digit(ch);
}

inline bool is_non_spacing_mark(char16_t ch) noexcept
{
    return vn_is_non_spacing_mark(ch);
}

inline std::u16string normalize_search(std::u16string_view input)
{
    return settings_search_normalize_impl(input);
}

constexpr std::u16string_view get_file_name(std::u16string_view path) noexcept
{
    for (size_t i = path.size(); i > 0; --i)
    {
        char16_t c = path[i - 1];
        if (c == u'\\' || c == u'/' || c == u':')
        {
            return path.substr(i);
        }
    }
    return path;
}

constexpr std::u16string_view get_extension(std::u16string_view path) noexcept
{
    for (size_t i = path.size(); i > 0; --i)
    {
        char16_t c = path[i - 1];
        if (c == u'.')
        {
            return (i != path.size()) ? path.substr(i - 1) : std::u16string_view{};
        }
        if (c == u'\\' || c == u'/' || c == u':')
        {
            break;
        }
    }
    return {};
}

constexpr std::u16string_view get_stem(std::u16string_view path) noexcept
{
    std::u16string_view name = get_file_name(path);
    size_t last_dot = name.rfind(u'.');
    return (last_dot == std::u16string_view::npos) ? name : name.substr(0, last_dot);
}

inline bool try_get_process_image_path(uint32_t process_id, std::u16string* out_path)
{
    out_path->clear();
    if (process_id == 0) return false;

    HANDLE raw_process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (raw_process == nullptr) return false;

    struct HandleGuard
    {
        HANDLE h;
        ~HandleGuard() noexcept
        {
            if (h != nullptr && h != INVALID_HANDLE_VALUE)
            {
                CloseHandle(h);
            }
        }
    } guard{raw_process};

    wchar_t stack_buf[1024];
    DWORD size = 1024;
    if (QueryFullProcessImageNameW(guard.h, 0, stack_buf, &size))
    {
        out_path->assign(reinterpret_cast<const char16_t*>(stack_buf), size);
        return true;
    }

    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER)
    {
        return false;
    }

    std::vector<wchar_t> heap_buf(32768);
    size = 32768;
    if (!QueryFullProcessImageNameW(guard.h, 0, heap_buf.data(), &size))
    {
        return false;
    }

    out_path->assign(reinterpret_cast<const char16_t*>(heap_buf.data()), size);
    return true;
}

} // namespace vnotch
