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

} // extern "C"
