#pragma once
#include "Utf16Text.h"
#include <charconv>
namespace vnotch::parsing {
enum class JsonKind : uint8_t
{
    Invalid = 0,
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null
};

struct JsonSpan
{
    JsonKind kind = JsonKind::Invalid;
    std::u16string_view raw{};
};

constexpr bool is_json_ws(char16_t ch) noexcept
{
    return ch == u' ' || ch == u'\t' || ch == u'\r' || ch == u'\n';
}

constexpr void skip_json_ws(std::u16string_view src, size_t& pos) noexcept
{
    while (pos < src.size() && is_json_ws(src[pos]))
    {
        ++pos;
    }
}

inline bool scan_json_string(std::u16string_view src, size_t& pos, std::u16string* decoded_out = nullptr)
{
    if (pos >= src.size() || src[pos] != u'"') return false;
    ++pos;
    if (decoded_out != nullptr) decoded_out->clear();

    while (pos < src.size())
    {
        const char16_t ch = src[pos++];
        if (ch == u'"') return true;
        if (ch < 0x20u) return false;

        if (ch == u'\\')
        {
            if (pos >= src.size()) return false;
            const char16_t esc = src[pos++];
            char16_t out_ch = 0;
            switch (esc)
            {
            case u'"': out_ch = u'"'; break;
            case u'\\': out_ch = u'\\'; break;
            case u'/': out_ch = u'/'; break;
            case u'b': out_ch = u'\b'; break;
            case u'f': out_ch = u'\f'; break;
            case u'n': out_ch = u'\n'; break;
            case u'r': out_ch = u'\r'; break;
            case u't': out_ch = u'\t'; break;
            case u'u':
            {
                if (pos + 4 > src.size()) return false;
                if (!is_ascii_hex(src[pos]) || !is_ascii_hex(src[pos + 1]) ||
                    !is_ascii_hex(src[pos + 2]) || !is_ascii_hex(src[pos + 3]))
                    return false;
                out_ch = static_cast<char16_t>(
                    (hex_value(src[pos]) << 12) |
                    (hex_value(src[pos + 1]) << 8) |
                    (hex_value(src[pos + 2]) << 4) |
                    hex_value(src[pos + 3]));
                pos += 4;
                if (out_ch >= 0xDC00u && out_ch <= 0xDFFFu) return false;
                if (out_ch >= 0xD800u && out_ch <= 0xDBFFu)
                {
                    if (pos + 6 > src.size() || src[pos] != u'\\' || src[pos + 1] != u'u')
                        return false;
                    if (!is_ascii_hex(src[pos + 2]) || !is_ascii_hex(src[pos + 3]) ||
                        !is_ascii_hex(src[pos + 4]) || !is_ascii_hex(src[pos + 5]))
                        return false;
                    const char16_t lo = static_cast<char16_t>(
                        (hex_value(src[pos + 2]) << 12) |
                        (hex_value(src[pos + 3]) << 8) |
                        (hex_value(src[pos + 4]) << 4) |
                        hex_value(src[pos + 5]));
                    if (lo < 0xDC00u || lo > 0xDFFFu) return false;
                    pos += 6;
                    if (decoded_out != nullptr)
                    {
                        decoded_out->push_back(out_ch);
                        decoded_out->push_back(lo);
                    }
                    continue;
                }
                break;
            }
            default:
                return false;
            }
            if (decoded_out != nullptr) decoded_out->push_back(out_ch);
            continue;
        }

        if (ch >= 0xDC00u && ch <= 0xDFFFu) return false;
        if (ch >= 0xD800u && ch <= 0xDBFFu)
        {
            if (pos >= src.size()) return false;
            const char16_t lo = src[pos++];
            if (lo < 0xDC00u || lo > 0xDFFFu) return false;
            if (decoded_out != nullptr)
            {
                decoded_out->push_back(ch);
                decoded_out->push_back(lo);
            }
            continue;
        }

        if (decoded_out != nullptr) decoded_out->push_back(ch);
    }
    return false;
}

inline bool scan_json_number(std::u16string_view src, size_t& pos) noexcept
{
    const size_t start = pos;
    if (pos < src.size() && src[pos] == u'-') ++pos;
    if (pos >= src.size()) return false;
    if (src[pos] == u'0')
    {
        ++pos;
    }
    else if (src[pos] >= u'1' && src[pos] <= u'9')
    {
        ++pos;
        while (pos < src.size() && src[pos] >= u'0' && src[pos] <= u'9') ++pos;
    }
    else
    {
        return false;
    }

    if (pos < src.size() && src[pos] == u'.')
    {
        ++pos;
        if (pos >= src.size() || src[pos] < u'0' || src[pos] > u'9') return false;
        while (pos < src.size() && src[pos] >= u'0' && src[pos] <= u'9') ++pos;
    }

    if (pos < src.size() && (src[pos] == u'e' || src[pos] == u'E'))
    {
        ++pos;
        if (pos < src.size() && (src[pos] == u'+' || src[pos] == u'-')) ++pos;
        if (pos >= src.size() || src[pos] < u'0' || src[pos] > u'9') return false;
        while (pos < src.size() && src[pos] >= u'0' && src[pos] <= u'9') ++pos;
    }

    return pos > start;
}

inline bool json_parse_value(std::u16string_view src, size_t& pos, JsonSpan& out, int depth = 0)
{
    if (depth > 64) return false;
    skip_json_ws(src, pos);
    if (pos >= src.size()) return false;

    const size_t start = pos;
    const char16_t ch = src[pos];

    if (ch == u'{')
    {
        ++pos;
        skip_json_ws(src, pos);
        if (pos < src.size() && src[pos] == u'}')
        {
            ++pos;
            out = {JsonKind::Object, src.substr(start, pos - start)};
            return true;
        }
        while (true)
        {
            skip_json_ws(src, pos);
            if (!scan_json_string(src, pos, nullptr)) return false;
            skip_json_ws(src, pos);
            if (pos >= src.size() || src[pos] != u':') return false;
            ++pos;
            JsonSpan child{};
            if (!json_parse_value(src, pos, child, depth + 1)) return false;
            skip_json_ws(src, pos);
            if (pos >= src.size()) return false;
            if (src[pos] == u'}')
            {
                ++pos;
                out = {JsonKind::Object, src.substr(start, pos - start)};
                return true;
            }
            if (src[pos] != u',') return false;
            ++pos;
        }
    }

    if (ch == u'[')
    {
        ++pos;
        skip_json_ws(src, pos);
        if (pos < src.size() && src[pos] == u']')
        {
            ++pos;
            out = {JsonKind::Array, src.substr(start, pos - start)};
            return true;
        }
        while (true)
        {
            JsonSpan elem{};
            if (!json_parse_value(src, pos, elem, depth + 1)) return false;
            skip_json_ws(src, pos);
            if (pos >= src.size()) return false;
            if (src[pos] == u']')
            {
                ++pos;
                out = {JsonKind::Array, src.substr(start, pos - start)};
                return true;
            }
            if (src[pos] != u',') return false;
            ++pos;
        }
    }

    if (ch == u'"')
    {
        if (!scan_json_string(src, pos, nullptr)) return false;
        out = {JsonKind::String, src.substr(start, pos - start)};
        return true;
    }

    if (ch == u't')
    {
        if (src.substr(pos, 4) != u"true") return false;
        pos += 4;
        out = {JsonKind::True, src.substr(start, 4)};
        return true;
    }

    if (ch == u'f')
    {
        if (src.substr(pos, 5) != u"false") return false;
        pos += 5;
        out = {JsonKind::False, src.substr(start, 5)};
        return true;
    }

    if (ch == u'n')
    {
        if (src.substr(pos, 4) != u"null") return false;
        pos += 4;
        out = {JsonKind::Null, src.substr(start, 4)};
        return true;
    }

    if (ch == u'-' || (ch >= u'0' && ch <= u'9'))
    {
        if (!scan_json_number(src, pos)) return false;
        out = {JsonKind::Number, src.substr(start, pos - start)};
        return true;
    }

    return false;
}

inline bool json_parse_document(std::u16string_view src, JsonSpan& out_root)
{
    size_t pos = 0;
    if (!json_parse_value(src, pos, out_root, 0)) return false;
    skip_json_ws(src, pos);
    return pos == src.size();
}

inline bool json_decode_string(JsonSpan val, std::u16string& out)
{
    if (val.kind != JsonKind::String) return false;
    size_t pos = 0;
    return scan_json_string(val.raw, pos, &out) && pos == val.raw.size();
}

inline bool json_try_get_property(JsonSpan obj, std::u16string_view target_key, JsonSpan& out_val)
{
    if (obj.kind != JsonKind::Object || obj.raw.size() < 2) return false;
    size_t pos = 1;
    skip_json_ws(obj.raw, pos);
    if (pos < obj.raw.size() && obj.raw[pos] == u'}') return false;

    bool found = false;
    std::u16string key;
    while (pos < obj.raw.size())
    {
        skip_json_ws(obj.raw, pos);
        if (!scan_json_string(obj.raw, pos, &key)) return false;
        skip_json_ws(obj.raw, pos);
        if (pos >= obj.raw.size() || obj.raw[pos] != u':') return false;
        ++pos;
        JsonSpan val{};
        if (!json_parse_value(obj.raw, pos, val, 1)) return false;
        if (key == target_key)
        {
            out_val = val;
            found = true;
        }
        skip_json_ws(obj.raw, pos);
        if (pos >= obj.raw.size()) return false;
        if (obj.raw[pos] == u'}') break;
        if (obj.raw[pos] != u',') return false;
        ++pos;
    }
    return found;
}

inline bool json_try_get_double(JsonSpan val, double& out)
{
    if (val.kind != JsonKind::Number || val.raw.empty()) return false;
    char stack_buf[128];
    std::string heap_buf;
    char* buf = stack_buf;
    const size_t len = val.raw.size();
    if (len > sizeof(stack_buf))
    {
        heap_buf.resize(len);
        buf = heap_buf.data();
    }
    for (size_t i = 0; i < len; ++i)
    {
        if (val.raw[i] >= 0x80u) return false;
        buf[i] = static_cast<char>(val.raw[i]);
    }
    double parsed = 0.0;
    const auto [ptr, ec] = std::from_chars(buf, buf + len, parsed);
    if (ec != std::errc{} || ptr != buf + len || !std::isfinite(parsed))
        return false;
    if (parsed == 0.0) parsed = 0.0;
    out = parsed;
    return true;
}

inline bool json_try_get_int64(JsonSpan val, int64_t& out) noexcept
{
    if (val.kind != JsonKind::Number || val.raw.empty() || val.raw.size() >= 32)
        return false;
    char buf[32];
    for (size_t i = 0; i < val.raw.size(); ++i)
    {
        const char16_t c = val.raw[i];
        if (c == u'.' || c == u'e' || c == u'E' || c >= 0x80u) return false;
        buf[i] = static_cast<char>(c);
    }
    int64_t parsed = 0;
    const auto [ptr, ec] = std::from_chars(buf, buf + val.raw.size(), parsed);
    if (ec != std::errc{} || ptr != buf + val.raw.size()) return false;
    out = parsed;
    return true;
}

inline bool json_try_get_int32(JsonSpan val, int32_t& out) noexcept
{
    int64_t v = 0;
    if (!json_try_get_int64(val, v)) return false;
    if (v < INT32_MIN || v > INT32_MAX) return false;
    out = static_cast<int32_t>(v);
    return true;
}

}
