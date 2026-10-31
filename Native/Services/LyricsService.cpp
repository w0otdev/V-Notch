#include "vnotch_common.h"
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class LyricsService final {
public:
    LyricsService() = delete;

    [[nodiscard]] static constexpr bool TryParsePositiveInt(std::u16string_view s, int32_t& out_val) noexcept {
        out_val = 0;
        const auto trimmed = vn_trim(s);
        if (trimmed.empty()) return false;
        size_t idx = 0;
        if (trimmed[0] == u'+') {
            idx = 1;
            if (trimmed.size() == 1) return false;
        }
        int64_t acc = 0;
        for (; idx < trimmed.size(); ++idx) {
            const char16_t ch = trimmed[idx];
            if (ch < u'0' || ch > u'9') return false;
            acc = acc * 10 + (ch - u'0');
            if (acc > 2147483647LL) return false;
        }
        out_val = static_cast<int32_t>(acc);
        return true;
    }

    [[nodiscard]] static constexpr bool TryParseTimestamp(
        std::u16string_view ts,
        int64_t& out_ticks) noexcept {
        out_ticks = 0;

        int32_t colon_idx = -1;
        int32_t dot_idx = -1;
        for (size_t i = 0; i < ts.size(); ++i) {
            if (colon_idx < 0 && ts[i] == u':') colon_idx = static_cast<int32_t>(i);
            if (dot_idx < 0 && ts[i] == u'.') dot_idx = static_cast<int32_t>(i);
        }
        if (colon_idx < 1 || dot_idx < colon_idx) return false;

        int32_t minutes = 0;
        if (!TryParsePositiveInt(ts.substr(0, static_cast<size_t>(colon_idx)), minutes)) {
            return false;
        }

        int32_t seconds = 0;
        if (!TryParsePositiveInt(
                ts.substr(static_cast<size_t>(colon_idx + 1), static_cast<size_t>(dot_idx - colon_idx - 1)),
                seconds)) {
            return false;
        }

        std::u16string_view frac_str = ts.substr(static_cast<size_t>(dot_idx + 1));
        if (frac_str.size() > 3) {
            frac_str = frac_str.substr(0, 3);
        }

        int32_t frac = 0;
        if (!TryParsePositiveInt(frac_str, frac)) {
            return false;
        }

        int32_t ms = frac;
        if (frac_str.size() == 1) ms = frac * 100;
        else if (frac_str.size() == 2) ms = frac * 10;

        // TimeSpan(0, 0, minutes, seconds, ms) in 100-ns ticks
        const int64_t total_ms = static_cast<int64_t>(minutes) * 60000LL +
                                 static_cast<int64_t>(seconds) * 1000LL +
                                 static_cast<int64_t>(ms);
        out_ticks = total_ms * 10000LL;
        return true;
    }

    [[nodiscard]] static constexpr bool TryParseLrcLine(
        std::u16string_view raw_line,
        int64_t& out_ticks,
        int32_t& out_text_start,
        int32_t& out_text_length) noexcept {
        out_ticks = 0;
        out_text_start = 0;
        out_text_length = 0;

        size_t line_start = 0;
        while (line_start < raw_line.size() && vn_is_whitespace(raw_line[line_start])) ++line_start;
        size_t line_end = raw_line.size();
        while (line_end > line_start && vn_is_whitespace(raw_line[line_end - 1])) --line_end;

        const std::u16string_view line = raw_line.substr(line_start, line_end - line_start);
        if (line.size() < 10 || line[0] != u'[') return false;

        int32_t close_bracket = -1;
        for (size_t i = 0; i < line.size(); ++i) {
            if (line[i] == u']') {
                close_bracket = static_cast<int32_t>(i);
                break;
            }
        }
        if (close_bracket < 5) return false;

        const std::u16string_view timestamp = line.substr(1, static_cast<size_t>(close_bracket - 1));
        size_t text_rel_start = static_cast<size_t>(close_bracket + 1);
        while (text_rel_start < line.size() && vn_is_whitespace(line[text_rel_start])) ++text_rel_start;
        size_t text_rel_end = line.size();
        while (text_rel_end > text_rel_start && vn_is_whitespace(line[text_rel_end - 1])) --text_rel_end;

        if (text_rel_end <= text_rel_start) return false;

        if (!TryParseTimestamp(timestamp, out_ticks)) return false;

        out_text_start = static_cast<int32_t>(line_start + text_rel_start);
        out_text_length = static_cast<int32_t>(text_rel_end - text_rel_start);
        return true;
    }

    [[nodiscard]] static constexpr bool ContainsWholePhrase(
        std::u16string_view text,
        std::u16string_view phrase) noexcept {
        if (phrase.size() < 3 || text.size() < phrase.size()) return false;
        size_t start = 0;
        const size_t max_start = text.size() - phrase.size();
        while (start <= max_start) {
            const size_t pos = text.find(phrase, start);
            if (pos == std::u16string_view::npos) return false;
            const size_t end = pos + phrase.size();
            if ((pos == 0 || text[pos - 1] == u' ') && (end == text.size() || text[end] == u' ')) {
                return true;
            }
            start = pos + 1;
        }
        return false;
    }

    [[nodiscard]] static constexpr bool IsGenericPlatformName(std::u16string_view artist) noexcept {
        return vn_equals_icase_ascii(artist, u"YouTube") ||
               vn_equals_icase_ascii(artist, u"Browser") ||
               vn_equals_icase_ascii(artist, u"Google Chrome") ||
               vn_equals_icase_ascii(artist, u"Microsoft Edge");
    }

    [[nodiscard]] static constexpr bool IsValidSyncLevel(std::u16string_view sync_level) noexcept {
        return vn_equals_icase_ascii(sync_level, u"word") ||
               vn_equals_icase_ascii(sync_level, u"line");
    }

    [[nodiscard]] static constexpr void CleanArtistSpan(
        std::u16string_view artist,
        int32_t& out_start,
        int32_t& out_length) noexcept {
        constexpr std::u16string_view kSeparators[] = {
            u" feat.", u" ft.", u" featuring", u" & ", u"; ", u" / ", u" with "
        };
        std::u16string_view s = artist;
        for (const auto sep : kSeparators) {
            if (s.size() >= sep.size()) {
                for (size_t i = 0; i <= s.size() - sep.size(); ++i) {
                    if (vn_equals_icase_ascii(s.substr(i, sep.size()), sep)) {
                        if (i > 0) {
                            s = s.substr(0, i);
                        }
                        break;
                    }
                }
            }
        }

        size_t start = 0;
        while (start < s.size() && vn_is_whitespace(s[start])) ++start;
        size_t end = s.size();
        while (end > start && vn_is_whitespace(s[end - 1])) --end;

        if (end > start) {
            out_start = static_cast<int32_t>(start);
            out_length = static_cast<int32_t>(end - start);
            return;
        }

        size_t orig_start = 0;
        while (orig_start < artist.size() && vn_is_whitespace(artist[orig_start])) ++orig_start;
        size_t orig_end = artist.size();
        while (orig_end > orig_start && vn_is_whitespace(artist[orig_end - 1])) --orig_end;
        out_start = static_cast<int32_t>(orig_start);
        out_length = static_cast<int32_t>(orig_end - orig_start);
    }
};

} // namespace vnotch::services

extern "C" {



VN_EXPORT int32_t vn_lyrics_try_parse_lrc_line(
    const char16_t* raw_line,
    int32_t raw_line_len,
    int64_t* out_ticks,
    int32_t* out_text_start,
    int32_t* out_text_length,
    uint8_t* out_ok) noexcept {
    if (!out_ticks || !out_text_start || !out_text_length || !out_ok || (!raw_line && raw_line_len > 0) || raw_line_len < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view sv(raw_line ? raw_line : u"", static_cast<size_t>(raw_line_len));
    int64_t ticks = 0;
    int32_t text_start = 0;
    int32_t text_length = 0;
    const bool ok = vnotch::services::LyricsService::TryParseLrcLine(sv, ticks, text_start, text_length);
    *out_ticks = ticks;
    *out_text_start = text_start;
    *out_text_length = text_length;
    *out_ok = ok ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_lyrics_contains_whole_phrase(
    const char16_t* text,
    int32_t text_len,
    const char16_t* phrase,
    int32_t phrase_len,
    uint8_t* out_contains) noexcept {
    if (!out_contains || (!text && text_len > 0) || text_len < 0 || (!phrase && phrase_len > 0) || phrase_len < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view t(text ? text : u"", static_cast<size_t>(text_len));
    const std::u16string_view p(phrase ? phrase : u"", static_cast<size_t>(phrase_len));
    *out_contains = vnotch::services::LyricsService::ContainsWholePhrase(t, p) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_lyrics_is_generic_platform_name(
    const char16_t* artist,
    int32_t artist_len,
    uint8_t* out_is_generic) noexcept {
    if (!out_is_generic || (!artist && artist_len > 0) || artist_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(artist ? artist : u"", static_cast<size_t>(artist_len));
    *out_is_generic = vnotch::services::LyricsService::IsGenericPlatformName(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_lyrics_is_valid_sync_level(
    const char16_t* sync_level,
    int32_t sync_level_len,
    uint8_t* out_is_valid) noexcept {
    if (!out_is_valid || (!sync_level && sync_level_len > 0) || sync_level_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(sync_level ? sync_level : u"", static_cast<size_t>(sync_level_len));
    *out_is_valid = vnotch::services::LyricsService::IsValidSyncLevel(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_lyrics_clean_artist_span(
    const char16_t* artist,
    int32_t artist_len,
    int32_t* out_start,
    int32_t* out_length) noexcept {
    if (!out_start || !out_length || (!artist && artist_len > 0) || artist_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(artist ? artist : u"", static_cast<size_t>(artist_len));
    int32_t start = 0;
    int32_t length = 0;
    vnotch::services::LyricsService::CleanArtistSpan(sv, start, length);
    *out_start = start;
    *out_length = length;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_lyrics_normalize_for_matching(
    const char16_t* text_ptr,
    int32_t text_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept {
    if (!out_written || text_len < 0 || (text_len > 0 && !text_ptr) || out_cap < 0 || (out_cap > 0 && !out_buf)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_written = 0;
    if (!text_ptr || text_len == 0) {
        return VN_OK;
    }
    const std::u16string_view sv(text_ptr, static_cast<size_t>(text_len));
    if (vnotch::is_null_or_whitespace(sv)) {
        return VN_OK;
    }
    try {
        std::u16string stripped;
        stripped.reserve(sv.size());
        for (size_t i = 0; i < sv.size(); ++i) {
            const char16_t ch = sv[i];
            if (ch == u'(' || ch == u'[') {
                size_t j = i + 1;
                while (j < sv.size() && sv[j] != u')' && sv[j] != u']') {
                    ++j;
                }
                if (j < sv.size()) {
                    i = j;
                    continue;
                }
            }
            stripped.push_back(ch);
        }

        const std::u16string decomposed = vnotch::normalize_unicode_form(stripped, NormalizationD);
        std::u16string filtered;
        filtered.reserve(decomposed.size());
        bool needs_recomposition = false;
        for (const char16_t original : decomposed) {
            if (original == 0x0111u || original == 0x0110u) {
                filtered.push_back(u'd');
                continue;
            }
            if (original > 127) {
                WORD ctype3 = 0;
                const wchar_t wch = static_cast<wchar_t>(original);
                if (vn_is_non_spacing_mark(original) ||
                    (GetStringTypeW(CT_CTYPE3, &wch, 1, &ctype3) && (ctype3 & C3_NONSPACING) != 0)) {
                    continue;
                }
            }
            filtered.push_back(original);
            needs_recomposition |= (original > 127);
        }

        if (needs_recomposition) {
            filtered = vnotch::normalize_unicode_form(filtered, NormalizationC);
        }

        std::u16string result;
        result.reserve(filtered.size());
        bool pending_space = false;
        for (const char16_t original : filtered) {
            const char16_t c = (original >= u'A' && original <= u'Z')
                ? static_cast<char16_t>(original + (u'a' - u'A'))
                : original;
            if ((c >= u'a' && c <= u'z') || (c >= u'0' && c <= u'9')) {
                if (pending_space) {
                    result.push_back(u' ');
                }
                result.push_back(c);
                pending_space = false;
            } else {
                pending_space = !result.empty();
            }
        }

        return vnotch::copy_out_u16(result, out_buf, out_cap, out_written);
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
