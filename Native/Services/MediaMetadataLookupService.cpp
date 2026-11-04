#include "../vnotch_common.h"
#include <algorithm>
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

    static constexpr bool IsAsciiWhitespace(char16_t c) noexcept {
        return c == u' ' || c == u'\t' || c == u'\r' || c == u'\n';
    }

    static constexpr size_t SkipWhitespace(std::u16string_view sv, size_t pos) noexcept {
        while (pos < sv.size() && IsAsciiWhitespace(sv[pos])) {
            ++pos;
        }
        return pos;
    }

    static constexpr int32_t HexValue(char16_t c) noexcept {
        if (c >= u'0' && c <= u'9') return static_cast<int32_t>(c - u'0');
        if (c >= u'a' && c <= u'f') return static_cast<int32_t>(c - u'a' + 10);
        if (c >= u'A' && c <= u'F') return static_cast<int32_t>(c - u'A' + 10);
        return -1;
    }

    static constexpr size_t FindEndOfJsonString(std::u16string_view sv, size_t pos) noexcept {
        while (pos < sv.size()) {
            char16_t c = sv[pos++];
            if (c == u'"') return pos;
            if (c == u'\\' && pos < sv.size()) {
                ++pos;
            }
        }
        return std::u16string_view::npos;
    }

    static constexpr size_t FindMatchingCloseBrace(std::u16string_view sv, size_t open_brace_pos) noexcept {
        if (open_brace_pos >= sv.size() || sv[open_brace_pos] != u'{') return std::u16string_view::npos;
        int32_t depth = 0;
        size_t pos = open_brace_pos;
        while (pos < sv.size()) {
            char16_t c = sv[pos++];
            if (c == u'"') {
                pos = FindEndOfJsonString(sv, pos);
                if (pos == std::u16string_view::npos) return sv.size();
            } else if (c == u'{') {
                ++depth;
            } else if (c == u'}') {
                --depth;
                if (depth == 0) return pos;
            }
        }
        return sv.size();
    }

    static constexpr bool DecodeJsonStringLiteral(
        std::u16string_view sv,
        size_t after_open_quote,
        char16_t* out_buf,
        int32_t cap,
        int32_t& in_out_len,
        size_t& out_after_close_quote) noexcept {
        size_t pos = after_open_quote;
        while (pos < sv.size()) {
            char16_t c = sv[pos++];
            if (c == u'"') {
                out_after_close_quote = pos;
                return true;
            }
            char16_t decoded = c;
            if (c == u'\\') {
                if (pos >= sv.size()) break;
                char16_t esc = sv[pos++];
                switch (esc) {
                    case u'"': decoded = u'"'; break;
                    case u'\\': decoded = u'\\'; break;
                    case u'/': decoded = u'/'; break;
                    case u'b': decoded = u'\b'; break;
                    case u'f': decoded = u'\f'; break;
                    case u'n': decoded = u'\n'; break;
                    case u'r': decoded = u'\r'; break;
                    case u't': decoded = u'\t'; break;
                    case u'u': {
                        if (pos + 4 <= sv.size()) {
                            int32_t h0 = HexValue(sv[pos]);
                            int32_t h1 = HexValue(sv[pos + 1]);
                            int32_t h2 = HexValue(sv[pos + 2]);
                            int32_t h3 = HexValue(sv[pos + 3]);
                            if (h0 >= 0 && h1 >= 0 && h2 >= 0 && h3 >= 0) {
                                decoded = static_cast<char16_t>((h0 << 12) | (h1 << 8) | (h2 << 4) | h3);
                                pos += 4;
                                break;
                            }
                        }
                        decoded = esc;
                        break;
                    }
                    default:
                        decoded = esc;
                        break;
                }
            }
            if (in_out_len < cap) {
                out_buf[in_out_len++] = decoded;
            }
        }
        out_after_close_quote = sv.size();
        return false;
    }

    static constexpr int32_t DecodeHtmlEntitiesInPlace(char16_t* buf, int32_t len) noexcept {
        int32_t read_idx = 0;
        int32_t write_idx = 0;
        while (read_idx < len) {
            if (buf[read_idx] == u'&') {
                int32_t max_semi = std::min(len, read_idx + 10);
                int32_t semi_idx = -1;
                for (int32_t i = read_idx + 2; i < max_semi; ++i) {
                    if (buf[i] == u';') {
                        semi_idx = i;
                        break;
                    }
                    if (buf[i] == u'&' || IsAsciiWhitespace(buf[i])) {
                        break;
                    }
                }
                if (semi_idx > read_idx + 1) {
                    std::u16string_view entity(buf + read_idx + 1, static_cast<size_t>(semi_idx - read_idx - 1));
                    char16_t replacement = 0;
                    if (entity == u"amp") replacement = u'&';
                    else if (entity == u"quot") replacement = u'"';
                    else if (entity == u"apos" || entity == u"#39" || entity == u"#039") replacement = u'\'';
                    else if (entity == u"lt") replacement = u'<';
                    else if (entity == u"gt") replacement = u'>';
                    else if (entity == u"nbsp") replacement = u' ';
                    else if (entity.size() >= 2 && entity[0] == u'#') {
                        uint32_t cp = 0;
                        bool ok = true;
                        if (entity[1] == u'x' || entity[1] == u'X') {
                            ok = entity.size() > 2;
                            for (size_t k = 2; k < entity.size(); ++k) {
                                int32_t hv = HexValue(entity[k]);
                                if (hv < 0) { ok = false; break; }
                                cp = (cp << 4) | static_cast<uint32_t>(hv);
                            }
                        } else {
                            for (size_t k = 1; k < entity.size(); ++k) {
                                if (entity[k] < u'0' || entity[k] > u'9') { ok = false; break; }
                                cp = cp * 10u + static_cast<uint32_t>(entity[k] - u'0');
                            }
                        }
                        if (ok && cp > 0 && cp <= 0xFFFFu) {
                            replacement = static_cast<char16_t>(cp);
                        }
                    }
                    if (replacement != 0) {
                        buf[write_idx++] = replacement;
                        read_idx = semi_idx + 1;
                        continue;
                    }
                }
            }
            buf[write_idx++] = buf[read_idx++];
        }
        return write_idx;
    }

    static constexpr bool ExtractNamedStringProperty(
        std::u16string_view obj_sv,
        std::u16string_view quoted_prop,
        char16_t* out_buf,
        int32_t cap,
        int32_t& out_len) noexcept {
        size_t pos = 0;
        while (pos < obj_sv.size()) {
            size_t found = obj_sv.find(quoted_prop, pos);
            if (found == std::u16string_view::npos) return false;
            size_t p = SkipWhitespace(obj_sv, found + quoted_prop.size());
            if (p < obj_sv.size() && obj_sv[p] == u':') {
                p = SkipWhitespace(obj_sv, p + 1);
                if (p < obj_sv.size() && obj_sv[p] == u'"') {
                    out_len = 0;
                    size_t after_close = p + 1;
                    if (DecodeJsonStringLiteral(obj_sv, p + 1, out_buf, cap, out_len, after_close) && out_len > 0) {
                        out_len = DecodeHtmlEntitiesInPlace(out_buf, out_len);
                        return out_len > 0;
                    }
                }
            }
            pos = found + quoted_prop.size();
        }
        return false;
    }

    static constexpr bool ExtractTextFromFieldObject(
        std::u16string_view obj_sv,
        bool allow_accessibility_label,
        char16_t* out_buf,
        int32_t cap,
        int32_t& out_len) noexcept {
        out_len = 0;
        constexpr std::u16string_view kRunsKey = u"\"runs\"";
        size_t runs_pos = obj_sv.find(kRunsKey);
        if (runs_pos != std::u16string_view::npos) {
            size_t p = SkipWhitespace(obj_sv, runs_pos + kRunsKey.size());
            if (p < obj_sv.size() && obj_sv[p] == u':') {
                p = SkipWhitespace(obj_sv, p + 1);
                if (p < obj_sv.size() && obj_sv[p] == u'[') {
                    ++p;
                    int32_t brace_depth = 0;
                    while (p < obj_sv.size()) {
                        char16_t c = obj_sv[p];
                        if (c == u'"') {
                            if (brace_depth == 1 && obj_sv.substr(p).starts_with(u"\"text\"")) {
                                size_t after_key = SkipWhitespace(obj_sv, p + 6);
                                if (after_key < obj_sv.size() && obj_sv[after_key] == u':') {
                                    size_t val_start = SkipWhitespace(obj_sv, after_key + 1);
                                    if (val_start < obj_sv.size() && obj_sv[val_start] == u'"') {
                                        size_t after_val = val_start + 1;
                                        DecodeJsonStringLiteral(obj_sv, val_start + 1, out_buf, cap, out_len, after_val);
                                        p = after_val;
                                        continue;
                                    }
                                }
                            }
                            p = FindEndOfJsonString(obj_sv, p + 1);
                            if (p == std::u16string_view::npos) break;
                            continue;
                        }
                        if (c == u'{') {
                            ++brace_depth;
                        } else if (c == u'}') {
                            --brace_depth;
                        } else if (c == u']' && brace_depth == 0) {
                            break;
                        }
                        ++p;
                    }
                    if (out_len > 0) {
                        out_len = DecodeHtmlEntitiesInPlace(out_buf, out_len);
                        return out_len > 0;
                    }
                }
            }
        }

        if (ExtractNamedStringProperty(obj_sv, u"\"simpleText\"", out_buf, cap, out_len)) {
            return true;
        }

        if (allow_accessibility_label && ExtractNamedStringProperty(obj_sv, u"\"label\"", out_buf, cap, out_len)) {
            return true;
        }

        return false;
    }

    static constexpr bool TryExtractFieldFromWindow(
        std::u16string_view window,
        std::u16string_view quoted_key,
        bool allow_accessibility_label,
        char16_t* out_buf,
        int32_t cap,
        int32_t& out_len) noexcept {
        size_t pos = 0;
        while (pos < window.size()) {
            size_t found = window.find(quoted_key, pos);
            if (found == std::u16string_view::npos) return false;
            size_t p = SkipWhitespace(window, found + quoted_key.size());
            if (p < window.size() && window[p] == u':') {
                p = SkipWhitespace(window, p + 1);
                if (p < window.size() && window[p] == u'{') {
                    size_t close_brace = FindMatchingCloseBrace(window, p);
                    if (close_brace != std::u16string_view::npos && close_brace > p) {
                        std::u16string_view obj_sv = window.substr(p, close_brace - p);
                        if (ExtractTextFromFieldObject(obj_sv, allow_accessibility_label, out_buf, cap, out_len)) {
                            return true;
                        }
                    }
                }
            }
            pos = found + quoted_key.size();
        }
        return false;
    }

    static constexpr bool TryReadVideoIdAt(
        std::u16string_view sv,
        size_t match_pos,
        std::u16string_view& out_id,
        size_t& out_after_id) noexcept {
        constexpr std::u16string_view kVideoIdKey = u"\"videoId\"";
        size_t p = SkipWhitespace(sv, match_pos + kVideoIdKey.size());
        if (p >= sv.size() || sv[p] != u':') return false;
        p = SkipWhitespace(sv, p + 1);
        if (p >= sv.size() || sv[p] != u'"') return false;
        ++p;
        if (p + 11 >= sv.size() || sv[p + 11] != u'"') return false;
        std::u16string_view id_sv = sv.substr(p, 11);
        if (!IsDirectVideoId(id_sv)) return false;
        out_id = id_sv;
        out_after_id = p + 12;
        return true;
    }

    static constexpr void NextScrapeCandidate(
        std::u16string_view json,
        int32_t start_pos,
        int32_t& out_next_pos,
        char16_t* out_video_id,
        int32_t video_id_cap,
        int32_t& out_video_id_len,
        char16_t* out_title,
        int32_t title_cap,
        int32_t& out_title_len,
        char16_t* out_channel,
        int32_t channel_cap,
        int32_t& out_channel_len) noexcept {
        out_video_id_len = 0;
        out_title_len = 0;
        out_channel_len = 0;
        out_next_pos = static_cast<int32_t>(json.size());

        if (start_pos < 0 || static_cast<size_t>(start_pos) >= json.size() || video_id_cap < 11) {
            return;
        }

        constexpr std::u16string_view kVideoIdKey = u"\"videoId\"";
        constexpr std::u16string_view kVideoRendererKey = u"\"videoRenderer\"";
        constexpr size_t kMaxRendererWindow = 32768;

        size_t pos = static_cast<size_t>(start_pos);
        while (pos < json.size()) {
            size_t match_pos = json.find(kVideoIdKey, pos);
            if (match_pos == std::u16string_view::npos) {
                return;
            }

            std::u16string_view video_id;
            size_t after_id = 0;
            if (!TryReadVideoIdAt(json, match_pos, video_id, after_id)) {
                pos = match_pos + kVideoIdKey.size();
                continue;
            }

            for (size_t i = 0; i < 11; ++i) {
                out_video_id[i] = video_id[i];
            }
            out_video_id_len = 11;
            out_next_pos = static_cast<int32_t>(after_id);

            size_t window_end = std::min(json.size(), after_id + kMaxRendererWindow);
            size_t next_renderer = json.substr(after_id, window_end - after_id).find(kVideoRendererKey);
            if (next_renderer != std::u16string_view::npos) {
                window_end = after_id + next_renderer;
            }

            size_t scan_other = after_id;
            while (scan_other < window_end) {
                size_t rel = json.substr(scan_other, window_end - scan_other).find(kVideoIdKey);
                if (rel == std::u16string_view::npos) break;
                size_t other_pos = scan_other + rel;
                std::u16string_view other_id;
                size_t other_after = 0;
                if (TryReadVideoIdAt(json, other_pos, other_id, other_after) && other_id != video_id) {
                    window_end = other_pos;
                    break;
                }
                scan_other = other_pos + kVideoIdKey.size();
            }

            std::u16string_view window = json.substr(after_id, window_end - after_id);
            TryExtractFieldFromWindow(window, u"\"title\"", true, out_title, title_cap, out_title_len);

            if (!TryExtractFieldFromWindow(window, u"\"ownerText\"", false, out_channel, channel_cap, out_channel_len) &&
                !TryExtractFieldFromWindow(window, u"\"longBylineText\"", false, out_channel, channel_cap, out_channel_len)) {
                TryExtractFieldFromWindow(window, u"\"shortBylineText\"", false, out_channel, channel_cap, out_channel_len);
            }
            return;
        }
    }

    static constexpr bool FindPrefixVideoId(
        std::u16string_view sv,
        std::u16string_view prefix,
        size_t& out_match_pos,
        int32_t& out_id_start) noexcept {
        size_t pos = 0;
        while (pos < sv.size()) {
            const size_t found = sv.find(prefix, pos);
            if (found == std::u16string_view::npos) return false;
            const size_t id_start = found + prefix.size();
            if (id_start + 11 <= sv.size() && IsDirectVideoId(sv.substr(id_start, 11))) {
                out_match_pos = found;
                out_id_start = static_cast<int32_t>(id_start);
                return true;
            }
            pos = found + 1;
        }
        return false;
    }

    static constexpr bool FindWatchParamVideoId(
        std::u16string_view sv,
        size_t& out_match_pos,
        int32_t& out_id_start) noexcept {
        constexpr std::u16string_view kWatch = u"youtube.com/watch?";
        size_t pos = 0;
        while (pos < sv.size()) {
            const size_t found = sv.find(kWatch, pos);
            if (found == std::u16string_view::npos) return false;
            const size_t search_from = found + kWatch.size();
            int32_t last_valid_id = -1;
            size_t v_pos = search_from;
            while (v_pos < sv.size()) {
                const size_t vf = sv.find(u"v=", v_pos);
                if (vf == std::u16string_view::npos) break;
                const size_t id_start = vf + 2;
                if (id_start + 11 <= sv.size() && IsDirectVideoId(sv.substr(id_start, 11))) {
                    last_valid_id = static_cast<int32_t>(id_start);
                }
                v_pos = vf + 1;
            }
            if (last_valid_id >= 0) {
                out_match_pos = found;
                out_id_start = last_valid_id;
                return true;
            }
            pos = found + 1;
        }
        return false;
    }

    static constexpr bool TryExtractYouTubeVideoIdFromUrl(
        std::u16string_view input,
        uint8_t mode,
        int32_t& out_start,
        int32_t& out_len) noexcept {
        out_start = 0;
        out_len = 0;
        if (input.size() < 11) return false;

        if (mode == 2) {
            size_t match_pos = 0;
            int32_t id_start = 0;
            if (FindPrefixVideoId(input, u"v=", match_pos, id_start)) {
                out_start = id_start;
                out_len = 11;
                return true;
            }
            return false;
        }

        size_t best_match_pos = std::u16string_view::npos;
        int32_t best_id_start = -1;

        auto consider = [&](size_t match_pos, int32_t id_start) noexcept {
            if (match_pos < best_match_pos) {
                best_match_pos = match_pos;
                best_id_start = id_start;
            }
        };

        size_t mp = 0;
        int32_t ids = 0;
        if (FindWatchParamVideoId(input, mp, ids)) consider(mp, ids);
        if (FindPrefixVideoId(input, u"youtu.be/", mp, ids)) consider(mp, ids);
        if (mode == 1) {
            if (FindPrefixVideoId(input, u"youtube.com/embed/", mp, ids)) consider(mp, ids);
            if (FindPrefixVideoId(input, u"youtube.com/v/", mp, ids)) consider(mp, ids);
        }

        if (best_id_start >= 0) {
            out_start = best_id_start;
            out_len = 11;
            return true;
        }
        return false;
    }

    static constexpr int32_t NormalizeTrackTokens(
        std::u16string_view src,
        char16_t* dst,
        int32_t cap) noexcept {
        int32_t len = 0;
        bool pending_space = false;
        for (size_t i = 0; i < src.size() && len < cap; ++i) {
            const char16_t ch = src[i];
            if (ch == u'(' || ch == u'[') {
                size_t close_idx = std::u16string_view::npos;
                for (size_t j = i + 1; j < src.size(); ++j) {
                    if (src[j] == u')' || src[j] == u']') {
                        close_idx = j;
                        break;
                    }
                }
                if (close_idx != std::u16string_view::npos) {
                    i = close_idx;
                    pending_space = (len > 0);
                    continue;
                }
            }
            const char16_t lower = (ch >= u'A' && ch <= u'Z')
                ? static_cast<char16_t>(ch + (u'a' - u'A'))
                : ch;
            const bool is_alnum = (lower >= u'a' && lower <= u'z') ||
                                  (lower >= u'0' && lower <= u'9') ||
                                  (lower > 127 && !vnotch::is_unicode_whitespace(lower));
            if (is_alnum) {
                if (pending_space && len < cap - 1) {
                    dst[len++] = u' ';
                }
                if (len < cap) {
                    dst[len++] = lower;
                }
                pending_space = false;
            } else if (len > 0) {
                pending_space = true;
            }
        }
        return len;
    }

    static constexpr bool ContainsWholePhraseNormalized(
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

    static constexpr void ScoreYouTubeTrackMatch(
        std::u16string_view candidate_title,
        std::u16string_view target_title,
        int64_t candidate_duration_sec,
        int64_t target_duration_sec,
        int32_t& out_score,
        int32_t& out_duration_delta,
        uint8_t& out_is_match) noexcept {
        out_score = 0;
        out_duration_delta = 1073741823;
        out_is_match = 0;

        char16_t cand_buf[512];
        char16_t targ_buf[512];
        const int32_t cand_len = NormalizeTrackTokens(candidate_title, cand_buf, 512);
        const int32_t targ_len = NormalizeTrackTokens(target_title, targ_buf, 512);
        const std::u16string_view cand_norm(cand_buf, static_cast<size_t>(cand_len));
        const std::u16string_view targ_norm(targ_buf, static_cast<size_t>(targ_len));

        int32_t title_score = 0;
        if (targ_norm.empty() || cand_norm.empty()) {
            title_score = (targ_norm.empty() && cand_norm.empty()) ? 50 : 0;
        } else if (cand_norm == targ_norm) {
            title_score = 100;
        } else if (ContainsWholePhraseNormalized(cand_norm, targ_norm) ||
                   ContainsWholePhraseNormalized(targ_norm, cand_norm)) {
            title_score = 80;
        } else {
            int32_t total_words = 0;
            int32_t matched_words = 0;
            size_t pos = 0;
            while (pos < targ_norm.size()) {
                size_t space = targ_norm.find(u' ', pos);
                if (space == std::u16string_view::npos) space = targ_norm.size();
                const std::u16string_view word = targ_norm.substr(pos, space - pos);
                if (word.size() >= 2) {
                    ++total_words;
                    if (ContainsWholePhraseNormalized(cand_norm, word) || cand_norm == word) {
                        ++matched_words;
                    }
                }
                pos = space + 1;
            }
            if (total_words > 0 && matched_words * 10 >= total_words * 6) {
                title_score = 60;
            }
        }

        if (candidate_duration_sec > 0 && target_duration_sec > 0) {
            const int64_t diff = candidate_duration_sec >= target_duration_sec
                ? (candidate_duration_sec - target_duration_sec)
                : (target_duration_sec - candidate_duration_sec);
            out_duration_delta = diff > 1073741823LL ? 1073741823 : static_cast<int32_t>(diff);
        }

        int32_t total_score = title_score;
        if (title_score > 0 && candidate_duration_sec > 0 && target_duration_sec > 0) {
            if (out_duration_delta <= 2) total_score += 20;
            else if (out_duration_delta <= 5) total_score += 10;
            else if (out_duration_delta > 15) total_score -= 30;
        }

        out_score = std::max(0, total_score);
        out_is_match = (title_score >= 60 && (candidate_duration_sec <= 0 || target_duration_sec <= 0 || out_duration_delta <= 15)) ? 1u : 0u;
    }

    static std::u16string ExtractFeaturingFromTitle(
        std::u16string_view title,
        std::u16string& out_stripped_title) noexcept {
        out_stripped_title.assign(title);
        std::u16string feat_artists;
        constexpr std::u16string_view markers[] = {
            u"(feat. ", u"[feat. ", u"(ft. ", u"[ft. ", u"(with ", u"[with "
        };
        for (auto marker : markers) {
            int32_t idx = vnotch::index_of_ignore_case(out_stripped_title, marker);
            if (idx >= 0) {
                char16_t close_ch = marker[0] == u'(' ? u')' : u']';
                size_t content_start = static_cast<size_t>(idx) + marker.size();
                size_t close_pos = out_stripped_title.find(close_ch, content_start);
                if (close_pos != std::u16string::npos) {
                    std::u16string_view inside = vnotch::trim_view(
                        std::u16string_view(out_stripped_title).substr(content_start, close_pos - content_start));
                    if (!inside.empty()) {
                        feat_artists.assign(inside);
                    }
                    out_stripped_title.erase(static_cast<size_t>(idx), close_pos - static_cast<size_t>(idx) + 1);
                    out_stripped_title = std::u16string(vnotch::trim_view(out_stripped_title));
                    break;
                }
            }
        }
        return feat_artists;
    }

    static bool HasExtraVersionTag(
        std::u16string_view norm_cand,
        std::u16string_view norm_exp) noexcept {
        constexpr std::u16string_view tags[] = {
            u"mix", u"remix", u"live", u"instrumental", u"karaoke", u"acoustic",
            u"sped up", u"slowed", u"reverb", u"lofi", u"lo fi", u"cover",
            u"demo", u"vip", u"flip", u"bootleg", u"mashup", u"nightcore", u"8d", u"edit"
        };
        for (auto tag : tags) {
            const bool cand_has = ContainsWholePhraseNormalized(norm_cand, tag) || norm_cand == tag;
            const bool exp_has = ContainsWholePhraseNormalized(norm_exp, tag) || norm_exp == tag;
            if (cand_has != exp_has) {
                return true;
            }
        }
        return false;
    }

    static void FormatSpotifyArtists(
        std::u16string_view raw_artist,
        std::u16string_view primary_artist,
        std::u16string_view cand_title,
        std::u16string_view exp_title,
        int64_t cand_dur_ms,
        int64_t exp_dur_ms,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t& out_len,
        int32_t& out_score) noexcept {
        out_len = 0;
        out_score = 0;
        if (raw_artist.empty() || primary_artist.empty() || cand_title.empty() || exp_title.empty() || !out_buf || out_cap <= 0) {
            return;
        }

        std::u16string stripped_cand_title;
        std::u16string title_feat = ExtractFeaturingFromTitle(cand_title, stripped_cand_title);

        std::u16string stripped_exp_title;
        (void)ExtractFeaturingFromTitle(exp_title, stripped_exp_title);

        const std::u16string norm_cand_full = vnotch::normalize_for_loose_match_impl(cand_title);
        const std::u16string norm_cand_stripped = vnotch::normalize_for_loose_match_impl(stripped_cand_title);
        const std::u16string norm_exp_full = vnotch::normalize_for_loose_match_impl(exp_title);
        const std::u16string norm_exp_stripped = vnotch::normalize_for_loose_match_impl(stripped_exp_title);
        if (norm_cand_stripped.empty() || norm_exp_stripped.empty()) return;

        // Strict canonical title equality: reject any candidate that adds/removes extra words or version tags
        if (norm_cand_full != norm_exp_full && norm_cand_stripped != norm_exp_stripped) {
            return;
        }
        if (HasExtraVersionTag(norm_cand_full, norm_exp_full)) {
            return;
        }

        int32_t title_score = (norm_cand_full == norm_exp_full) ? 105 : 100;

        if (cand_dur_ms > 0 && exp_dur_ms > 0) {
            const int64_t diff_ms = cand_dur_ms >= exp_dur_ms ? (cand_dur_ms - exp_dur_ms) : (exp_dur_ms - cand_dur_ms);
            if (diff_ms > 4000) return;
            if (diff_ms <= 1500) title_score += 25;
            else title_score += 10;
        }

        const std::u16string norm_raw_artist = vnotch::normalize_for_loose_match_impl(raw_artist);
        const std::u16string norm_primary = vnotch::normalize_for_loose_match_impl(primary_artist);
        if (norm_raw_artist.empty() || norm_primary.empty()) {
            return;
        }
        // Primary artist must match as exact or whole phrase at the start of raw_artist
        if (norm_raw_artist != norm_primary &&
            !vnotch::starts_with_ordinal(norm_raw_artist, norm_primary) &&
            !ContainsWholePhraseNormalized(norm_raw_artist, norm_primary)) {
            return;
        }

        std::u16string combined_raw(raw_artist);
        if (!title_feat.empty()) {
            const std::u16string norm_feat = vnotch::normalize_for_loose_match_impl(title_feat);
            if (!norm_feat.empty() && !vnotch::contains_ordinal(norm_raw_artist, norm_feat)) {
                combined_raw.append(u", ");
                combined_raw.append(title_feat);
            }
        }

        const bool primary_has_amp = vnotch::contains_ordinal(primary_artist, u" & ");
        std::u16string formatted;
        formatted.reserve(combined_raw.size() + 4);
        for (size_t i = 0; i < combined_raw.size();) {
            if (!primary_has_amp && i + 3 <= combined_raw.size() &&
                combined_raw[i] == u' ' && combined_raw[i + 1] == u'&' && combined_raw[i + 2] == u' ') {
                formatted.append(u", ");
                i += 3;
                continue;
            }
            if (i + 6 <= combined_raw.size() &&
                vnotch::equals_ignore_case(std::u16string_view(combined_raw).substr(i, 6), u" feat.")) {
                formatted.append(u",");
                i += 6;
                continue;
            }
            if (i + 4 <= combined_raw.size() &&
                vnotch::equals_ignore_case(std::u16string_view(combined_raw).substr(i, 4), u" ft.")) {
                formatted.append(u",");
                i += 4;
                continue;
            }
            formatted.push_back(combined_raw[i++]);
        }

        const std::u16string_view trimmed = vnotch::trim_view(formatted);
        if (trimmed.size() <= primary_artist.size() ||
            vnotch::equals_ignore_case(trimmed, primary_artist) ||
            !vnotch::contains_ordinal(trimmed, u", ")) {
            return;
        }

        // First artist before ", " must match primary_artist (normalized) unless primary_artist itself is a group in trimmed
        size_t first_comma = trimmed.find(u", ");
        if (first_comma != std::u16string_view::npos && !primary_has_amp) {
            const std::u16string norm_first = vnotch::normalize_for_loose_match_impl(trimmed.substr(0, first_comma));
            if (norm_first != norm_primary) {
                return;
            }
        }

        const int32_t copy_len = std::min(static_cast<int32_t>(trimmed.size()), out_cap);
        for (int32_t i = 0; i < copy_len; ++i) {
            out_buf[i] = trimmed[static_cast<size_t>(i)];
        }
        out_len = copy_len;
        out_score = title_score + 35;
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

VN_EXPORT int32_t __stdcall vn_yt_next_scrape_candidate(
    const char16_t* json,
    int32_t json_len,
    int32_t start_pos,
    int32_t* out_next_pos,
    char16_t* out_video_id,
    int32_t video_id_cap,
    int32_t* out_video_id_len,
    char16_t* out_title,
    int32_t title_cap,
    int32_t* out_title_len,
    char16_t* out_channel,
    int32_t channel_cap,
    int32_t* out_channel_len) noexcept {
    if (!out_next_pos || !out_video_id || !out_video_id_len ||
        !out_title || !out_title_len || !out_channel || !out_channel_len ||
        json_len < 0 || start_pos < 0 || video_id_cap < 11 || title_cap <= 0 || channel_cap <= 0 ||
        (!json && json_len > 0)) {
        return VN_E_INVALID_ARG;
    }
    try {
        std::u16string_view sv(json ? json : u"", static_cast<size_t>(json_len));
        vnotch::services::MediaMetadataLookupService::NextScrapeCandidate(
            sv,
            start_pos,
            *out_next_pos,
            out_video_id,
            video_id_cap,
            *out_video_id_len,
            out_title,
            title_cap,
            *out_title_len,
            out_channel,
            channel_cap,
            *out_channel_len);
        return VN_OK;
    } catch (...) {
        *out_video_id_len = 0;
        *out_title_len = 0;
        *out_channel_len = 0;
        return -3;
    }
}

VN_EXPORT int32_t __stdcall vn_media_metadata_extract_youtube_video_id(
    const char16_t* input,
    int32_t input_len,
    uint8_t mode,
    int32_t* out_start,
    int32_t* out_len,
    uint8_t* out_found) noexcept {
    if (!out_start || !out_len || !out_found || input_len < 0 || (!input && input_len > 0)) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view sv(input ? input : u"", static_cast<size_t>(input_len));
    *out_found = vnotch::services::MediaMetadataLookupService::TryExtractYouTubeVideoIdFromUrl(
        sv, mode, *out_start, *out_len) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_metadata_score_youtube_track_match(
    const char16_t* candidate_title,
    int32_t candidate_title_len,
    const char16_t* target_title,
    int32_t target_title_len,
    int64_t candidate_duration_sec,
    int64_t target_duration_sec,
    int32_t* out_score,
    int32_t* out_duration_delta,
    uint8_t* out_is_match) noexcept {
    if (!out_score || !out_duration_delta || !out_is_match ||
        candidate_title_len < 0 || (!candidate_title && candidate_title_len > 0) ||
        target_title_len < 0 || (!target_title && target_title_len > 0)) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view cand_sv(candidate_title ? candidate_title : u"", static_cast<size_t>(candidate_title_len));
    const std::u16string_view targ_sv(target_title ? target_title : u"", static_cast<size_t>(target_title_len));
    vnotch::services::MediaMetadataLookupService::ScoreYouTubeTrackMatch(
        cand_sv, targ_sv, candidate_duration_sec, target_duration_sec,
        *out_score, *out_duration_delta, *out_is_match);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_media_metadata_format_spotify_artists(
    const char16_t* raw_artist,
    int32_t raw_artist_len,
    const char16_t* primary_artist,
    int32_t primary_artist_len,
    const char16_t* cand_title,
    int32_t cand_title_len,
    const char16_t* exp_title,
    int32_t exp_title_len,
    int64_t cand_dur_ms,
    int64_t exp_dur_ms,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len,
    int32_t* out_score) noexcept {
    if (!out_buf || out_cap <= 0 || !out_len || !out_score ||
        raw_artist_len < 0 || primary_artist_len < 0 || cand_title_len < 0 || exp_title_len < 0) {
        return VN_E_INVALID_ARG;
    }
    try {
        vnotch::services::MediaMetadataLookupService::FormatSpotifyArtists(
            std::u16string_view(raw_artist ? raw_artist : u"", static_cast<size_t>(raw_artist_len)),
            std::u16string_view(primary_artist ? primary_artist : u"", static_cast<size_t>(primary_artist_len)),
            std::u16string_view(cand_title ? cand_title : u"", static_cast<size_t>(cand_title_len)),
            std::u16string_view(exp_title ? exp_title : u"", static_cast<size_t>(exp_title_len)),
            cand_dur_ms,
            exp_dur_ms,
            out_buf,
            out_cap,
            *out_len,
            *out_score);
        return VN_OK;
    } catch (...) {
        *out_len = 0;
        *out_score = 0;
        return VN_ERR_FAIL;
    }
}

} // extern "C"
