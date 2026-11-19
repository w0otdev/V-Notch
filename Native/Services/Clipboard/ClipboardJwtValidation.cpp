#include "vnotch_common.h"
#include "ClipboardJwtValidation.h"

namespace vnotch::services
{
    static inline int32_t base64url_val(char16_t c) noexcept
    {
        if (c >= u'A' && c <= u'Z') return c - u'A';
        if (c >= u'a' && c <= u'z') return 26 + (c - u'a');
        if (c >= u'0' && c <= u'9') return 52 + (c - u'0');
        if (c == u'-' || c == u'+') return 62;
        if (c == u'_' || c == u'/') return 63;
        return -1;
    }

    static bool decode_base64url(std::u16string_view part, uint8_t* out_bytes, size_t out_cap, size_t& out_len) noexcept
    {
        out_len = 0;
        while (!part.empty() && part.back() == u'=')
        {
            part.remove_suffix(1);
        }
        if (part.empty() || (part.size() % 4) == 1)
        {
            return false;
        }
        size_t i = 0;
        size_t w = 0;
        while (i + 4 <= part.size())
        {
            const int32_t v0 = base64url_val(part[i]);
            const int32_t v1 = base64url_val(part[i + 1]);
            const int32_t v2 = base64url_val(part[i + 2]);
            const int32_t v3 = base64url_val(part[i + 3]);
            if ((v0 | v1 | v2 | v3) < 0 || w + 3 > out_cap) return false;
            const uint32_t triple = (static_cast<uint32_t>(v0) << 18) |
                                    (static_cast<uint32_t>(v1) << 12) |
                                    (static_cast<uint32_t>(v2) << 6) |
                                    static_cast<uint32_t>(v3);
            out_bytes[w++] = static_cast<uint8_t>((triple >> 16) & 0xFFu);
            out_bytes[w++] = static_cast<uint8_t>((triple >> 8) & 0xFFu);
            out_bytes[w++] = static_cast<uint8_t>(triple & 0xFFu);
            i += 4;
        }
        const size_t rem = part.size() - i;
        if (rem == 2)
        {
            const int32_t v0 = base64url_val(part[i]);
            const int32_t v1 = base64url_val(part[i + 1]);
            if ((v0 | v1) < 0 || w + 1 > out_cap) return false;
            out_bytes[w++] = static_cast<uint8_t>(((v0 << 2) | (v1 >> 4)) & 0xFF);
        }
        else if (rem == 3)
        {
            const int32_t v0 = base64url_val(part[i]);
            const int32_t v1 = base64url_val(part[i + 1]);
            const int32_t v2 = base64url_val(part[i + 2]);
            if ((v0 | v1 | v2) < 0 || w + 2 > out_cap) return false;
            const uint32_t pair = (static_cast<uint32_t>(v0) << 10) |
                                  (static_cast<uint32_t>(v1) << 4) |
                                  (static_cast<uint32_t>(v2) >> 2);
            out_bytes[w++] = static_cast<uint8_t>((pair >> 8) & 0xFFu);
            out_bytes[w++] = static_cast<uint8_t>(pair & 0xFFu);
        }
        out_len = w;
        return w > 0;
    }

    struct JsonCursor
    {
        const uint8_t* data;
        size_t len;
        size_t pos;
        bool has_non_empty_alg;

        constexpr void skip_ws() noexcept
        {
            while (pos < len)
            {
                const uint8_t b = data[pos];
                if (b == ' ' || b == '\t' || b == '\r' || b == '\n') ++pos;
                else break;
            }
        }

        bool parse_string(std::string_view* out_raw = nullptr) noexcept
        {
            if (pos >= len || data[pos] != '"') return false;
            ++pos;
            const size_t start = pos;
            while (pos < len)
            {
                const uint8_t b = data[pos++];
                if (b == '"')
                {
                    if (out_raw)
                    {
                        *out_raw = std::string_view(reinterpret_cast<const char*>(data + start), (pos - 1) - start);
                    }
                    return true;
                }
                if (b < 0x20u) return false;
                if (b == '\\')
                {
                    if (pos >= len) return false;
                    const uint8_t esc = data[pos++];
                    switch (esc)
                    {
                        case '"': case '\\': case '/': case 'b':
                        case 'f': case 'n': case 'r': case 't':
                            break;
                        case 'u':
                            if (pos + 4 > len) return false;
                            for (int k = 0; k < 4; ++k)
                            {
                                const uint8_t h = data[pos++];
                                const bool ok = (h >= '0' && h <= '9') || (h >= 'a' && h <= 'f') || (h >= 'A' && h <= 'F');
                                if (!ok) return false;
                            }
                            break;
                        default:
                            return false;
                    }
                }
            }
            return false;
        }

        bool parse_number() noexcept
        {
            if (pos < len && data[pos] == '-') ++pos;
            if (pos >= len) return false;
            if (data[pos] == '0')
            {
                ++pos;
            }
            else if (data[pos] >= '1' && data[pos] <= '9')
            {
                while (pos < len && data[pos] >= '0' && data[pos] <= '9') ++pos;
            }
            else
            {
                return false;
            }
            if (pos < len && data[pos] == '.')
            {
                ++pos;
                if (pos >= len || data[pos] < '0' || data[pos] > '9') return false;
                while (pos < len && data[pos] >= '0' && data[pos] <= '9') ++pos;
            }
            if (pos < len && (data[pos] == 'e' || data[pos] == 'E'))
            {
                ++pos;
                if (pos < len && (data[pos] == '+' || data[pos] == '-')) ++pos;
                if (pos >= len || data[pos] < '0' || data[pos] > '9') return false;
                while (pos < len && data[pos] >= '0' && data[pos] <= '9') ++pos;
            }
            return true;
        }

        bool parse_value(int32_t depth) noexcept
        {
            if (depth > 16) return false;
            skip_ws();
            if (pos >= len) return false;
            const uint8_t b = data[pos];
            if (b == '{') return parse_object(depth + 1);
            if (b == '[') return parse_array(depth + 1);
            if (b == '"') return parse_string(nullptr);
            if (b == '-' || (b >= '0' && b <= '9')) return parse_number();
            if (b == 't' && pos + 4 <= len && std::memcmp(data + pos, "true", 4) == 0) { pos += 4; return true; }
            if (b == 'f' && pos + 5 <= len && std::memcmp(data + pos, "false", 5) == 0) { pos += 5; return true; }
            if (b == 'n' && pos + 4 <= len && std::memcmp(data + pos, "null", 4) == 0) { pos += 4; return true; }
            return false;
        }

        bool parse_array(int32_t depth) noexcept
        {
            if (depth > 16 || pos >= len || data[pos] != '[') return false;
            ++pos;
            skip_ws();
            if (pos < len && data[pos] == ']') { ++pos; return true; }
            while (true)
            {
                if (!parse_value(depth)) return false;
                skip_ws();
                if (pos < len && data[pos] == ',')
                {
                    ++pos;
                    continue;
                }
                if (pos < len && data[pos] == ']')
                {
                    ++pos;
                    return true;
                }
                return false;
            }
        }

        bool parse_object(int32_t depth) noexcept
        {
            if (depth > 16 || pos >= len || data[pos] != '{') return false;
            ++pos;
            skip_ws();
            if (pos < len && data[pos] == '}') { ++pos; return true; }
            while (true)
            {
                skip_ws();
                std::string_view key{};
                if (!parse_string(&key)) return false;
                skip_ws();
                if (pos >= len || data[pos] != ':') return false;
                ++pos;
                skip_ws();
                if (depth == 1 && key == "alg")
                {
                    if (pos < len && data[pos] == '"')
                    {
                        std::string_view val{};
                        if (!parse_string(&val)) return false;
                        has_non_empty_alg = !val.empty();
                    }
                    else
                    {
                        has_non_empty_alg = false;
                        if (!parse_value(depth)) return false;
                    }
                }
                else
                {
                    if (!parse_value(depth)) return false;
                }
                skip_ws();
                if (pos < len && data[pos] == ',')
                {
                    ++pos;
                    continue;
                }
                if (pos < len && data[pos] == '}')
                {
                    ++pos;
                    return true;
                }
                return false;
            }
        }
    };

    static bool is_valid_json_object(const uint8_t* utf8, size_t len, bool require_alg) noexcept
    {
        if (len < 2) return false;
        bool all_ascii = true;
        for (size_t i = 0; i < len; ++i)
        {
            if ((utf8[i] & 0x80u) != 0)
            {
                all_ascii = false;
                break;
            }
        }
        if (!all_ascii &&
            MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(utf8), static_cast<int>(len), nullptr, 0) <= 0)
        {
            return false;
        }
        if (len >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 += 3;
            len -= 3;
        }
        JsonCursor cur{ utf8, len, 0, false };
        cur.skip_ws();
        if (cur.pos >= cur.len || cur.data[cur.pos] != '{') return false;
        if (!cur.parse_object(1)) return false;
        cur.skip_ws();
        if (cur.pos != cur.len) return false;
        return !require_alg || cur.has_non_empty_alg;
    }

    bool VerifyJwtSegments(
        std::u16string_view p0,
        std::u16string_view p1,
        std::u16string_view p2) noexcept
    {
        uint8_t scratch_buf[12288];
        size_t decoded_len = 0;
        if (!decode_base64url(p0, scratch_buf, sizeof(scratch_buf), decoded_len) ||
            !is_valid_json_object(scratch_buf, decoded_len, true))
        {
            return false;
        }
        decoded_len = 0;
        if (!decode_base64url(p1, scratch_buf, sizeof(scratch_buf), decoded_len) ||
            !is_valid_json_object(scratch_buf, decoded_len, false))
        {
            return false;
        }
        decoded_len = 0;
        return decode_base64url(p2, scratch_buf, sizeof(scratch_buf), decoded_len) && decoded_len > 0;
    }
}
