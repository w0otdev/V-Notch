#include "vnotch_common.h"
#include "LyricsInternal.h"
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

    [[nodiscard]] static constexpr void CleanArtistSpan(
        std::u16string_view artist,
        int32_t& out_start,
        int32_t& out_length) noexcept {
        constexpr std::u16string_view kSeparators[] = {
            u" feat.", u" ft.", u" featuring", u" & ", u", ", u"; ", u" / ", u" with "
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

    [[nodiscard]] static constexpr bool StartsWithPatternCultureAware(
        std::u16string_view sv,
        std::u16string_view pattern,
        bool is_turkic,
        bool i_matches_both_cases = false) noexcept {
        if (sv.size() < pattern.size()) return false;
        for (size_t k = 0; k < pattern.size(); ++k) {
            const char16_t ch = sv[k];
            const char16_t p = pattern[k];
            if (p == u'i') {
                if (i_matches_both_cases || !is_turkic) {
                    if (ch != u'i' && ch != u'I' && ch != u'\u0130') return false;
                } else {
                    if (ch != u'i' && ch != u'\u0130') return false;
                }
            } else if (p == u'I') {
                if (i_matches_both_cases || !is_turkic) {
                    if (ch != u'i' && ch != u'I' && ch != u'\u0130') return false;
                } else {
                    if (ch != u'I' && ch != u'\u0131') return false;
                }
            } else {
                const char16_t lower_ch = (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
                const char16_t lower_p = (p >= u'A' && p <= u'Z') ? static_cast<char16_t>(p + 32) : p;
                if (lower_ch != lower_p) return false;
            }
        }
        return true;
    }

    [[nodiscard]] static constexpr bool StartsWithTrackNoDigits(std::u16string_view sv) noexcept {
        if (!vn_starts_with_icase_ascii(sv, u"track")) return false;
        size_t pos = 5;
        while (pos < sv.size() && vn_is_whitespace(sv[pos])) ++pos;
        if (!vn_starts_with_icase_ascii(sv.substr(pos), u"no.")) return false;
        pos += 3;
        return pos < sv.size() && sv[pos] >= u'0' && sv[pos] <= u'9';
    }

    [[nodiscard]] static constexpr bool IsVideoPipeSuffix(std::u16string_view sv, bool is_turkic) noexcept {
        return StartsWithPatternCultureAware(sv, u"Official", is_turkic, true) ||
               StartsWithPatternCultureAware(sv, u"MV", is_turkic, true) ||
               StartsWithPatternCultureAware(sv, u"Music Video", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Visualizer", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Lyric Video", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Audio", is_turkic, false) ||
               StartsWithTrackNoDigits(sv);
    }

    [[nodiscard]] static constexpr bool IsRecordingDashSuffix(std::u16string_view sv, bool is_turkic) noexcept {
        return StartsWithPatternCultureAware(sv, u"Remaster", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Live", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Acoustic", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Radio Edit", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Bonus Track", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Single Version", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Instrumental", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Deluxe", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Mono", is_turkic, false) ||
               StartsWithPatternCultureAware(sv, u"Stereo", is_turkic, false);
    }

    [[nodiscard]] static constexpr bool ContainsSubstringIcase(
        std::u16string_view haystack,
        std::u16string_view needle) noexcept {
        if (needle.empty() || haystack.size() < needle.size()) return false;
        const size_t max_i = haystack.size() - needle.size();
        for (size_t i = 0; i <= max_i; ++i) {
            bool match = true;
            for (size_t j = 0; j < needle.size(); ++j) {
                char16_t a = haystack[i + j];
                char16_t b = needle[j];
                if (a >= u'A' && a <= u'Z') a += 32;
                if (b >= u'A' && b <= u'Z') b += 32;
                if (a != b) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [[nodiscard]] static constexpr bool HasEditionMismatch(
        std::u16string_view target_raw,
        std::u16string_view cand_raw) noexcept {
        constexpr std::u16string_view kAsciiKeywords[] = {
            u"live", u"remix", u"cover", u"acoustic", u"instrumental",
            u"karaoke", u"sped up", u"slowed", u"demo", u"nightcore", u"reverb", u"bootleg", u"edit"
        };
        for (const auto kw : kAsciiKeywords) {
            // Check word-bounded in candidate vs target
            const bool cand_has = ContainsAsciiWordIcase(cand_raw, kw);
            if (cand_has && !ContainsAsciiWordIcase(target_raw, kw)) return true;
        }
        constexpr std::u16string_view kCjkKeywords[] = {
            u"伴奏", u"翻唱", u"现场", u"純音樂", u"纯音乐", u"DJ版", u"慢摇"
        };
        for (const auto kw : kCjkKeywords) {
            if (ContainsSubstringIcase(cand_raw, kw) && !ContainsSubstringIcase(target_raw, kw)) return true;
        }
        return false;
    }

    [[nodiscard]] static constexpr bool ContainsAsciiWordIcase(
        std::u16string_view text,
        std::u16string_view word) noexcept {
        if (word.empty() || text.size() < word.size()) return false;
        const size_t max_i = text.size() - word.size();
        for (size_t i = 0; i <= max_i; ++i) {
            if (!vn_equals_icase_ascii(text.substr(i, word.size()), word)) continue;
            const bool left_ok = (i == 0) || !IsAsciiAlnum(text[i - 1]);
            const size_t end = i + word.size();
            const bool right_ok = (end == text.size()) || !IsAsciiAlnum(text[end]);
            if (left_ok && right_ok) return true;
        }
        return false;
    }

    [[nodiscard]] static constexpr bool IsAsciiAlnum(char16_t ch) noexcept {
        return (ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z') || (ch >= u'0' && ch <= u'9');
    }

    [[nodiscard]] static constexpr bool IsCreditHeaderLine(
        std::u16string_view text,
        int64_t time_ticks) noexcept {
        const auto trimmed = vn_trim(text);
        if (trimmed.empty()) return true;
        if (trimmed.front() == u'{' && trimmed.back() == u'}') return true;
        // Credit lines usually appear in the first 15 seconds and contain ':' or '：'
        if (time_ticks > 150000000LL) return false;
        int32_t colon_pos = -1;
        for (size_t i = 0; i < trimmed.size() && i < 24; ++i) {
            if (trimmed[i] == u':' || trimmed[i] == u'：') {
                colon_pos = static_cast<int32_t>(i);
                break;
            }
        }
        if (colon_pos <= 0) return false;
        const auto prefix = vn_trim(trimmed.substr(0, static_cast<size_t>(colon_pos)));
        constexpr std::u16string_view kCreditPrefixes[] = {
            u"作词", u"作曲", u"编曲", u"制作人", u"混音", u"母带", u"和声", u"吉他", u"录音", u"统筹",
            u"监制", u"出品", u"发行", u"词", u"曲", u"原唱", u"翻唱", u"歌词贡献者", u"曲绘",
            u"lyricist", u"composer", u"arranger", u"producer", u"written by", u"produced by"
        };
        for (const auto kw : kCreditPrefixes) {
            if (ContainsSubstringIcase(prefix, kw)) return true;
        }
        return false;
    }

    [[nodiscard]] static constexpr bool DecodeKrcPayload(
        const uint8_t* raw_bytes,
        int32_t raw_len,
        uint8_t* out_zlib_bytes,
        int32_t& out_zlib_len) noexcept {
        out_zlib_len = 0;
        if (!raw_bytes || !out_zlib_bytes || raw_len <= 4) return false;
        if (raw_bytes[0] != 'k' || raw_bytes[1] != 'r' || raw_bytes[2] != 'c' || raw_bytes[3] != '1') {
            return false;
        }
        constexpr uint8_t kKrcKey[16] = {
            64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105
        };
        const int32_t payload_len = raw_len - 4;
        for (int32_t i = 0; i < payload_len; ++i) {
            out_zlib_bytes[i] = static_cast<uint8_t>(raw_bytes[i + 4] ^ kKrcKey[i & 15]);
        }
        out_zlib_len = payload_len;
        return true;
    }

    [[nodiscard]] static constexpr bool TryParseKrcLine(
        std::u16string_view raw_line,
        int64_t& out_line_ticks,
        char16_t* out_text_buf,
        int32_t out_text_cap,
        int32_t& out_text_len,
        VnLyricWord* out_words,
        int32_t out_words_cap,
        int32_t& out_words_count) noexcept {
        out_line_ticks = 0;
        out_text_len = 0;
        out_words_count = 0;

        const auto line = vn_trim(raw_line);
        if (line.size() < 6 || line[0] != u'[') return false;

        size_t close_bracket = std::u16string_view::npos;
        size_t comma_pos = std::u16string_view::npos;
        for (size_t i = 1; i < line.size(); ++i) {
            if (comma_pos == std::u16string_view::npos && line[i] == u',') comma_pos = i;
            if (line[i] == u']') { close_bracket = i; break; }
        }
        if (close_bracket == std::u16string_view::npos || comma_pos == std::u16string_view::npos || comma_pos >= close_bracket) {
            return false;
        }

        int32_t line_start_ms = 0;
        if (!TryParsePositiveInt(line.substr(1, comma_pos - 1), line_start_ms)) return false;
        out_line_ticks = static_cast<int64_t>(line_start_ms) * 10000LL;

        size_t pos = close_bracket + 1;
        while (pos < line.size()) {
            if (line[pos] == u'<') {
                size_t close_angle = std::u16string_view::npos;
                size_t c1 = std::u16string_view::npos;
                size_t c2 = std::u16string_view::npos;
                for (size_t j = pos + 1; j < line.size(); ++j) {
                    if (line[j] == u',') {
                        if (c1 == std::u16string_view::npos) c1 = j;
                        else if (c2 == std::u16string_view::npos) c2 = j;
                    } else if (line[j] == u'>') {
                        close_angle = j;
                        break;
                    }
                }
                if (close_angle != std::u16string_view::npos && c1 != std::u16string_view::npos) {
                    int32_t w_rel_ms = 0;
                    int32_t w_dur_ms = 0;
                    const size_t dur_end = (c2 != std::u16string_view::npos && c2 < close_angle) ? c2 : close_angle;
                    if (TryParsePositiveInt(line.substr(pos + 1, c1 - (pos + 1)), w_rel_ms) &&
                        TryParsePositiveInt(line.substr(c1 + 1, dur_end - (c1 + 1)), w_dur_ms)) {
                        size_t next_angle = close_angle + 1;
                        while (next_angle < line.size() && line[next_angle] != u'<') ++next_angle;
                        const std::u16string_view word_sv = line.substr(close_angle + 1, next_angle - (close_angle + 1));
                        const int32_t word_offset = out_text_len;
                        for (size_t k = 0; k < word_sv.size(); ++k) {
                            if (out_text_len >= out_text_cap) return false;
                            out_text_buf[out_text_len++] = word_sv[k];
                        }
                        if (!word_sv.empty() && out_words && out_words_count < out_words_cap) {
                            const int64_t ws_ms = static_cast<int64_t>(line_start_ms) + static_cast<int64_t>(w_rel_ms);
                            const int64_t we_ms = ws_ms + static_cast<int64_t>(w_dur_ms > 0 ? w_dur_ms : 0);
                            out_words[out_words_count++] = VnLyricWord{
                                ws_ms * 10000LL,
                                we_ms * 10000LL,
                                word_offset,
                                static_cast<int32_t>(word_sv.size())
                            };
                        }
                        pos = next_angle;
                        continue;
                    }
                }
            }
            if (out_text_len >= out_text_cap) return false;
            out_text_buf[out_text_len++] = line[pos++];
        }

        int32_t trim_lead = 0;
        while (trim_lead < out_text_len && vn_is_whitespace(out_text_buf[trim_lead])) ++trim_lead;
        int32_t trim_end = out_text_len;
        while (trim_end > trim_lead && vn_is_whitespace(out_text_buf[trim_end - 1])) --trim_end;
        if (trim_end <= trim_lead) return false;

        const int32_t final_len = trim_end - trim_lead;
        if (trim_lead > 0) {
            for (int32_t i = 0; i < final_len; ++i) {
                out_text_buf[i] = out_text_buf[trim_lead + i];
            }
        }
        out_text_len = final_len;

        int32_t valid_words = 0;
        for (int32_t i = 0; i < out_words_count; ++i) {
            int32_t off = out_words[i].offset - trim_lead;
            int32_t len = out_words[i].length;
            if (off < 0) { len += off; off = 0; }
            if (off + len > final_len) len = final_len - off;
            if (len > 0 && off >= 0 && off + len <= final_len) {
                out_words[valid_words] = out_words[i];
                out_words[valid_words].offset = off;
                out_words[valid_words].length = len;
                ++valid_words;
            }
        }
        out_words_count = valid_words;

        if (IsCreditHeaderLine(std::u16string_view(out_text_buf, static_cast<size_t>(out_text_len)), out_line_ticks)) {
            return false;
        }
        return true;
    }

    [[nodiscard]] static constexpr bool TryParseYrcLine(
        std::u16string_view raw_line,
        int64_t& out_line_ticks,
        char16_t* out_text_buf,
        int32_t out_text_cap,
        int32_t& out_text_len,
        VnLyricWord* out_words,
        int32_t out_words_cap,
        int32_t& out_words_count) noexcept {
        out_line_ticks = 0;
        out_text_len = 0;
        out_words_count = 0;

        const auto line = vn_trim(raw_line);
        if (line.size() < 6 || line[0] != u'[') return false;

        size_t close_bracket = std::u16string_view::npos;
        size_t comma_pos = std::u16string_view::npos;
        for (size_t i = 1; i < line.size(); ++i) {
            if (comma_pos == std::u16string_view::npos && line[i] == u',') comma_pos = i;
            if (line[i] == u']') { close_bracket = i; break; }
        }
        if (close_bracket == std::u16string_view::npos || comma_pos == std::u16string_view::npos || comma_pos >= close_bracket) {
            return false;
        }

        int32_t line_start_ms = 0;
        if (!TryParsePositiveInt(line.substr(1, comma_pos - 1), line_start_ms)) return false;
        out_line_ticks = static_cast<int64_t>(line_start_ms) * 10000LL;

        size_t pos = close_bracket + 1;
        while (pos < line.size()) {
            if (line[pos] == u'(') {
                size_t close_paren = std::u16string_view::npos;
                size_t c1 = std::u16string_view::npos;
                size_t c2 = std::u16string_view::npos;
                for (size_t j = pos + 1; j < line.size(); ++j) {
                    if (line[j] == u',') {
                        if (c1 == std::u16string_view::npos) c1 = j;
                        else if (c2 == std::u16string_view::npos) c2 = j;
                    } else if (line[j] == u')') {
                        close_paren = j;
                        break;
                    }
                }
                if (close_paren != std::u16string_view::npos && c1 != std::u16string_view::npos) {
                    int32_t w_start_ms = 0;
                    int32_t w_dur_ms = 0;
                    const size_t dur_end = (c2 != std::u16string_view::npos && c2 < close_paren) ? c2 : close_paren;
                    if (TryParsePositiveInt(line.substr(pos + 1, c1 - (pos + 1)), w_start_ms) &&
                        TryParsePositiveInt(line.substr(c1 + 1, dur_end - (c1 + 1)), w_dur_ms)) {
                        size_t next_paren = close_paren + 1;
                        while (next_paren < line.size() && line[next_paren] != u'(') ++next_paren;
                        const std::u16string_view word_sv = line.substr(close_paren + 1, next_paren - (close_paren + 1));
                        const int32_t word_offset = out_text_len;
                        for (size_t k = 0; k < word_sv.size(); ++k) {
                            if (out_text_len >= out_text_cap) return false;
                            out_text_buf[out_text_len++] = word_sv[k];
                        }
                        if (!word_sv.empty() && out_words && out_words_count < out_words_cap) {
                            const int64_t ws_ticks = static_cast<int64_t>(w_start_ms) * 10000LL;
                            const int64_t we_ticks = static_cast<int64_t>(w_start_ms + (w_dur_ms > 0 ? w_dur_ms : 0)) * 10000LL;
                            out_words[out_words_count++] = VnLyricWord{
                                ws_ticks,
                                we_ticks >= ws_ticks ? we_ticks : ws_ticks,
                                word_offset,
                                static_cast<int32_t>(word_sv.size())
                            };
                        }
                        pos = next_paren;
                        continue;
                    }
                }
            }
            if (out_text_len >= out_text_cap) return false;
            out_text_buf[out_text_len++] = line[pos++];
        }

        // Trim leading/trailing whitespace while shifting word offsets
        int32_t trim_lead = 0;
        while (trim_lead < out_text_len && vn_is_whitespace(out_text_buf[trim_lead])) ++trim_lead;
        int32_t trim_end = out_text_len;
        while (trim_end > trim_lead && vn_is_whitespace(out_text_buf[trim_end - 1])) --trim_end;
        if (trim_end <= trim_lead) return false;

        const int32_t final_len = trim_end - trim_lead;
        if (trim_lead > 0) {
            for (int32_t i = 0; i < final_len; ++i) {
                out_text_buf[i] = out_text_buf[trim_lead + i];
            }
        }
        out_text_len = final_len;

        int32_t valid_words = 0;
        for (int32_t i = 0; i < out_words_count; ++i) {
            int32_t off = out_words[i].offset - trim_lead;
            int32_t len = out_words[i].length;
            if (off < 0) { len += off; off = 0; }
            if (off + len > final_len) len = final_len - off;
            if (len > 0 && off >= 0 && off + len <= final_len) {
                out_words[valid_words] = out_words[i];
                out_words[valid_words].offset = off;
                out_words[valid_words].length = len;
                ++valid_words;
            }
        }
        out_words_count = valid_words;

        if (IsCreditHeaderLine(std::u16string_view(out_text_buf, static_cast<size_t>(out_text_len)), out_line_ticks)) {
            return false;
        }
        return true;
    }

    static constexpr int32_t StripTitleExtras(
        std::u16string_view title,
        bool is_turkic,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t& out_written) noexcept {
        out_written = 0;
        if (title.empty()) return VN_OK;
        if (out_cap < static_cast<int32_t>(title.size())) {
            out_written = static_cast<int32_t>(title.size());
            return VN_ERR_BUFFER_TOO_SMALL;
        }
        if (!out_buf) return VN_ERR_INVALID_ARG;

        if (vnotch::is_null_or_whitespace(title)) {
            for (size_t i = 0; i < title.size(); ++i) {
                out_buf[i] = title[i];
            }
            out_written = static_cast<int32_t>(title.size());
            return VN_OK;
        }

        int32_t len = 0;
        for (size_t i = 0; i < title.size(); ++i) {
            const char16_t ch = title[i];
            if (ch == u'(' || ch == u'[') {
                size_t close_pos = std::u16string_view::npos;
                for (size_t j = i + 1; j < title.size(); ++j) {
                    if (title[j] == u')' || title[j] == u']') {
                        close_pos = j;
                        break;
                    }
                }
                if (close_pos != std::u16string_view::npos) {
                    while (len > 0 && vn_is_whitespace(out_buf[len - 1])) {
                        --len;
                    }
                    i = close_pos;
                    continue;
                }
            }
            out_buf[len++] = ch;
        }

        for (int32_t i = 0; i < len; ++i) {
            if (out_buf[i] == u'|') {
                int32_t p = i + 1;
                while (p < len && vn_is_whitespace(out_buf[p])) ++p;
                const std::u16string_view rem(out_buf + p, static_cast<size_t>(len - p));
                if (IsVideoPipeSuffix(rem, is_turkic)) {
                    int32_t cut = i;
                    while (cut > 0 && vn_is_whitespace(out_buf[cut - 1])) --cut;
                    len = cut;
                    break;
                }
            }
        }

        for (int32_t i = 0; i < len; ++i) {
            if (out_buf[i] == u'-') {
                int32_t p = i + 1;
                while (p < len && vn_is_whitespace(out_buf[p])) ++p;
                const std::u16string_view rem(out_buf + p, static_cast<size_t>(len - p));
                if (IsRecordingDashSuffix(rem, is_turkic)) {
                    int32_t cut = i;
                    while (cut > 0 && vn_is_whitespace(out_buf[cut - 1])) --cut;
                    len = cut;
                    break;
                }
            }
        }

        int32_t start = 0;
        while (start < len && vn_is_whitespace(out_buf[start])) ++start;
        int32_t end = len;
        while (end > start && vn_is_whitespace(out_buf[end - 1])) --end;

        if (end > start) {
            const int32_t final_len = end - start;
            if (start > 0) {
                for (int32_t i = 0; i < final_len; ++i) {
                    out_buf[i] = out_buf[start + i];
                }
            }
            out_written = final_len;
            return VN_OK;
        }

        const std::u16string_view fallback = vn_trim(title);
        for (size_t i = 0; i < fallback.size(); ++i) {
            out_buf[i] = fallback[i];
        }
        out_written = static_cast<int32_t>(fallback.size());
        return VN_OK;
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

VN_EXPORT int32_t __stdcall vn_lyrics_strip_title_extras(
    const char16_t* title_ptr,
    int32_t title_len,
    uint8_t is_turkic_culture,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept {
    if (!out_written || title_len < 0 || (title_len > 0 && !title_ptr) || out_cap < 0 || (out_cap > 0 && !out_buf)) {
        return VN_ERR_INVALID_ARG;
    }
    const std::u16string_view sv(title_ptr ? title_ptr : u"", static_cast<size_t>(title_len));
    return vnotch::services::LyricsService::StripTitleExtras(sv, is_turkic_culture != 0, out_buf, out_cap, *out_written);
}

VN_EXPORT int32_t __stdcall vn_lyrics_is_credit_header_line(
    const char16_t* text_ptr,
    int32_t text_len,
    int64_t time_ticks,
    uint8_t* out_is_credit) noexcept {
    if (!out_is_credit || text_len < 0 || (text_len > 0 && !text_ptr)) return VN_ERR_INVALID_ARG;
    const std::u16string_view sv(text_ptr ? text_ptr : u"", static_cast<size_t>(text_len));
    *out_is_credit = vnotch::services::LyricsService::IsCreditHeaderLine(sv, time_ticks) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_lyrics_decode_krc_payload(
    const uint8_t* raw_bytes,
    int32_t raw_len,
    uint8_t* out_zlib_bytes,
    int32_t* out_zlib_len) noexcept {
    if (!out_zlib_len || raw_len < 0 || (raw_len > 0 && (!raw_bytes || !out_zlib_bytes))) {
        return VN_ERR_INVALID_ARG;
    }
    return vnotch::services::LyricsService::DecodeKrcPayload(raw_bytes, raw_len, out_zlib_bytes, *out_zlib_len)
        ? VN_OK : VN_ERR_FAIL;
}

} // extern "C"

namespace vnotch::services::lyrics_internal {

int32_t TryParseYrcLine(
    const char16_t* raw_line,
    int32_t raw_line_len,
    int64_t* out_line_ticks,
    char16_t* out_text_buf,
    int32_t out_text_cap,
    int32_t* out_text_len,
    VnLyricWord* out_words,
    int32_t out_words_cap,
    int32_t* out_words_count,
    uint8_t* out_ok) noexcept {
    if (!out_line_ticks || !out_text_len || !out_words_count || !out_ok ||
        raw_line_len < 0 || (raw_line_len > 0 && !raw_line) ||
        out_text_cap <= 0 || !out_text_buf || out_words_cap < 0 || (out_words_cap > 0 && !out_words)) {
        return VN_ERR_INVALID_ARG;
    }
    const std::u16string_view sv(raw_line ? raw_line : u"", static_cast<size_t>(raw_line_len));
    const bool ok = vnotch::services::LyricsService::TryParseYrcLine(
        sv, *out_line_ticks, out_text_buf, out_text_cap, *out_text_len, out_words, out_words_cap, *out_words_count);
    *out_ok = ok ? 1 : 0;
    return VN_OK;
}

int32_t ScoreNetEaseCandidate(
    const char16_t* target_title_ptr,
    int32_t target_title_len,
    const char16_t* target_artist_ptr,
    int32_t target_artist_len,
    int32_t target_duration_sec,
    const char16_t* cand_title_ptr,
    int32_t cand_title_len,
    const char16_t* cand_artists_ptr,
    int32_t cand_artists_len,
    int32_t cand_duration_ms,
    int32_t* out_score,
    uint8_t* out_accepted) noexcept {
    if (!out_score || !out_accepted ||
        target_title_len < 0 || target_artist_len < 0 || cand_title_len < 0 || cand_artists_len < 0) {
        return VN_ERR_INVALID_ARG;
    }
    *out_score = -1;
    *out_accepted = 0;

    const std::u16string_view t_title_raw(target_title_ptr ? target_title_ptr : u"", static_cast<size_t>(target_title_len));
    const std::u16string_view t_artist_raw(target_artist_ptr ? target_artist_ptr : u"", static_cast<size_t>(target_artist_len));
    const std::u16string_view c_title_raw(cand_title_ptr ? cand_title_ptr : u"", static_cast<size_t>(cand_title_len));
    const std::u16string_view c_artists_raw(cand_artists_ptr ? cand_artists_ptr : u"", static_cast<size_t>(cand_artists_len));

    if (vnotch::services::LyricsService::HasEditionMismatch(t_title_raw, c_title_raw)) {
        return VN_OK;
    }

    // Duration gate: when target duration > 0, require candidate duration within 3500 ms
    int32_t delta_ms = 0;
    if (target_duration_sec > 0) {
        if (cand_duration_ms <= 0) return VN_OK;
        const int32_t target_ms = target_duration_sec * 1000;
        delta_ms = cand_duration_ms >= target_ms ? (cand_duration_ms - target_ms) : (target_ms - cand_duration_ms);
        if (delta_ms > 3500) return VN_OK;
    }

    char16_t t_title_buf[512];
    char16_t c_title_buf[512];
    int32_t t_title_norm_len = 0;
    int32_t c_title_norm_len = 0;
    vn_lyrics_normalize_for_matching(t_title_raw.data(), static_cast<int32_t>(t_title_raw.size()), t_title_buf, 512, &t_title_norm_len);
    vn_lyrics_normalize_for_matching(c_title_raw.data(), static_cast<int32_t>(c_title_raw.size()), c_title_buf, 512, &c_title_norm_len);

    const std::u16string_view t_title_norm(t_title_buf, static_cast<size_t>(t_title_norm_len > 0 ? t_title_norm_len : 0));
    const std::u16string_view c_title_norm(c_title_buf, static_cast<size_t>(c_title_norm_len > 0 ? c_title_norm_len : 0));

    bool exact_title = false;
    bool phrase_title = false;
    if (!t_title_norm.empty() && !c_title_norm.empty()) {
        exact_title = (t_title_norm == c_title_norm);
        phrase_title = exact_title ||
            vnotch::services::LyricsService::ContainsWholePhrase(c_title_norm, t_title_norm) ||
            vnotch::services::LyricsService::ContainsWholePhrase(t_title_norm, c_title_norm);
    } else {
        // Non-Latin (CJK) fallback: compare trimmed raw titles
        const auto tt = vnotch::vn_trim(t_title_raw);
        const auto ct = vnotch::vn_trim(c_title_raw);
        exact_title = !tt.empty() && (tt == ct);
        phrase_title = exact_title || (!tt.empty() && !ct.empty() &&
            (vnotch::services::LyricsService::ContainsSubstringIcase(ct, tt) ||
             vnotch::services::LyricsService::ContainsSubstringIcase(tt, ct)));
    }
    if (!phrase_title) return VN_OK;

    // Artist verification
    bool artist_matched = false;
    bool exact_artist = false;
    if (vnotch::is_null_or_whitespace(t_artist_raw) ||
        vnotch::services::LyricsService::IsGenericPlatformName(vnotch::vn_trim(t_artist_raw))) {
        artist_matched = true;
    } else {
        int32_t prim_start = 0, prim_len = 0;
        vnotch::services::LyricsService::CleanArtistSpan(t_artist_raw, prim_start, prim_len);
        const std::u16string_view t_prim_raw = t_artist_raw.substr(static_cast<size_t>(prim_start), static_cast<size_t>(prim_len));

        char16_t t_prim_buf[256];
        int32_t t_prim_norm_len = 0;
        vn_lyrics_normalize_for_matching(t_prim_raw.data(), static_cast<int32_t>(t_prim_raw.size()), t_prim_buf, 256, &t_prim_norm_len);
        const std::u16string_view t_prim_norm(t_prim_buf, static_cast<size_t>(t_prim_norm_len > 0 ? t_prim_norm_len : 0));

        // Iterate pipe/comma-separated candidate artists
        size_t pos = 0;
        while (pos <= c_artists_raw.size()) {
            size_t next = c_artists_raw.find(u'|', pos);
            if (next == std::u16string_view::npos) next = c_artists_raw.size();
            const auto seg_raw = vnotch::vn_trim(c_artists_raw.substr(pos, next - pos));
            if (!seg_raw.empty()) {
                char16_t seg_buf[256];
                int32_t seg_norm_len = 0;
                vn_lyrics_normalize_for_matching(seg_raw.data(), static_cast<int32_t>(seg_raw.size()), seg_buf, 256, &seg_norm_len);
                const std::u16string_view seg_norm(seg_buf, static_cast<size_t>(seg_norm_len > 0 ? seg_norm_len : 0));
                if (!t_prim_norm.empty() && !seg_norm.empty()) {
                    if (seg_norm == t_prim_norm) {
                        artist_matched = true;
                        exact_artist = true;
                        break;
                    }
                    if (vnotch::services::LyricsService::ContainsWholePhrase(seg_norm, t_prim_norm) ||
                        vnotch::services::LyricsService::ContainsWholePhrase(t_prim_norm, seg_norm)) {
                        artist_matched = true;
                    }
                } else if (vnotch::services::LyricsService::ContainsSubstringIcase(seg_raw, t_prim_raw) ||
                           vnotch::services::LyricsService::ContainsSubstringIcase(t_prim_raw, seg_raw)) {
                    artist_matched = true;
                    exact_artist = (seg_raw == t_prim_raw);
                }
            }
            pos = next + 1;
        }
    }

    if (!artist_matched) return VN_OK;

    int32_t score = 10000 - delta_ms;
    if (exact_title) score += 2000;
    if (exact_artist) score += 1000;
    *out_score = score;
    *out_accepted = 1;
    return VN_OK;
}

int32_t TryParseKrcLine(
    const char16_t* raw_line,
    int32_t raw_line_len,
    int64_t* out_line_ticks,
    char16_t* out_text_buf,
    int32_t out_text_cap,
    int32_t* out_text_len,
    VnLyricWord* out_words,
    int32_t out_words_cap,
    int32_t* out_words_count,
    uint8_t* out_ok) noexcept {
    if (!out_line_ticks || !out_text_len || !out_words_count || !out_ok ||
        raw_line_len < 0 || (raw_line_len > 0 && !raw_line) ||
        out_text_cap <= 0 || !out_text_buf || out_words_cap < 0 || (out_words_cap > 0 && !out_words)) {
        return VN_ERR_INVALID_ARG;
    }
    const std::u16string_view sv(raw_line ? raw_line : u"", static_cast<size_t>(raw_line_len));
    const bool ok = vnotch::services::LyricsService::TryParseKrcLine(
        sv, *out_line_ticks, out_text_buf, out_text_cap, *out_text_len, out_words, out_words_cap, *out_words_count);
    *out_ok = ok ? 1 : 0;
    return VN_OK;
}

} // namespace vnotch::services::lyrics_internal
