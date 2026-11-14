#include "../vnotch_ext_sub.h"
#include "../vnotch_common.h"

#include <charconv>

// Forward declarations for existing exports defined in Native/Services/Translation/SelectedPrice.cpp
VN_EXPORT int32_t vn_currency_supported(
    const char16_t* code_ptr, int32_t code_len, uint8_t* out_supported) noexcept;

VN_EXPORT int32_t vn_currency_resolve_token(
    const char16_t* token_ptr, int32_t token_len,
    const char16_t* source_ptr, int32_t source_len,
    const char16_t* local_dollar_ptr, int32_t local_dollar_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept;

VN_EXPORT int32_t vn_currency_normalize_amount(
    const char16_t* value_ptr, int32_t value_len,
    const char16_t* currency_ptr, int32_t currency_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_valid) noexcept;

VN_EXPORT int32_t vn_currency_supported(const char16_t* code_ptr, int32_t code_len, uint8_t* out_supported) noexcept;
VN_EXPORT int32_t vn_currency_resolve_token(const char16_t* token_ptr, int32_t token_len, const char16_t* source_ptr, int32_t source_len, const char16_t* local_dollar_ptr, int32_t local_dollar_len, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept;
VN_EXPORT int32_t vn_currency_normalize_amount(const char16_t* value_ptr, int32_t value_len, const char16_t* currency_ptr, int32_t currency_len, char16_t* out_buf, int32_t out_cap, int32_t* out_len, uint8_t* out_valid) noexcept;

namespace
{

constexpr std::u16string_view make_u16_view(const wchar_t* ptr, int32_t len) noexcept
{
    return (ptr != nullptr && len > 0)
        ? std::u16string_view(reinterpret_cast<const char16_t*>(ptr), static_cast<size_t>(len))
        : std::u16string_view{};
}

int32_t write_wstr_out(std::u16string_view src, wchar_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (out_len == nullptr) return VN_ERR_INVALID_ARG;
    const int32_t needed = static_cast<int32_t>(src.size());
    *out_len = needed;
    if (needed == 0) return VN_OK;
    if (out_buf == nullptr || out_cap < needed) return VN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out_buf, src.data(), static_cast<size_t>(needed) * sizeof(wchar_t));
    return VN_OK;
}

constexpr char16_t ascii_to_lower(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
}

constexpr bool is_ascii_alpha(char16_t ch) noexcept
{
    return (ch >= u'A' && ch <= u'Z') || (ch >= u'a' && ch <= u'z');
}

constexpr bool is_ascii_alnum(char16_t ch) noexcept
{
    return is_ascii_alpha(ch) || (ch >= u'0' && ch <= u'9');
}

constexpr bool is_ascii_hex(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') ||
           (ch >= u'a' && ch <= u'f') ||
           (ch >= u'A' && ch <= u'F');
}

constexpr uint16_t hex_value(char16_t ch) noexcept
{
    if (ch >= u'0' && ch <= u'9') return static_cast<uint16_t>(ch - u'0');
    if (ch >= u'a' && ch <= u'f') return static_cast<uint16_t>(10 + (ch - u'a'));
    return static_cast<uint16_t>(10 + (ch - u'A'));
}

constexpr bool equals_ascii_icase(std::u16string_view a, std::u16string_view b) noexcept
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (ascii_to_lower(a[i]) != ascii_to_lower(b[i])) return false;
    }
    return true;
}

// Sorted BMP non-ASCII Nd (Decimal_Digit_Number) 10-digit block bases.
constexpr uint16_t kNonAsciiNdBases[36] = {
    0x0660u, 0x06F0u, 0x07C0u, 0x0966u, 0x09E6u, 0x0A66u, 0x0AE6u, 0x0B66u, 0x0BE6u,
    0x0C66u, 0x0CE6u, 0x0D66u, 0x0DE6u, 0x0E50u, 0x0ED0u, 0x0F20u, 0x1040u, 0x1090u,
    0x17E0u, 0x1810u, 0x1946u, 0x19D0u, 0x1A80u, 0x1A90u, 0x1B50u, 0x1BB0u, 0x1C40u,
    0x1C50u, 0xA620u, 0xA8D0u, 0xA900u, 0xA9D0u, 0xA9F0u, 0xAA50u, 0xABF0u, 0xFF10u
};

constexpr bool is_nd_digit(char16_t ch) noexcept
{
    if (ch < 0x80u) return ch >= u'0' && ch <= u'9';
    const uint16_t code = static_cast<uint16_t>(ch);
    int32_t lo = 0;
    int32_t hi = 35;
    while (lo <= hi)
    {
        const int32_t mid = lo + ((hi - lo) >> 1);
        const uint16_t base = kNonAsciiNdBases[mid];
        if (code < base)
            hi = mid - 1;
        else if (code > static_cast<uint16_t>(base + 9u))
            lo = mid + 1;
        else
            return true;
    }
    return false;
}

constexpr bool is_unicode_letter(char16_t ch) noexcept
{
    if (ch < 0x80u) return is_ascii_alpha(ch);
    return vn_is_letter_or_digit(ch) && !is_nd_digit(ch);
}

// Sorted BMP Nl | No ranges for \p{N}.
constexpr VnCharRange kUnicodeNlNoRanges[33] = {
    {0x00B2u, 0x00B3u}, {0x00B9u, 0x00B9u}, {0x00BCu, 0x00BEu}, {0x09F4u, 0x09F9u},
    {0x0B72u, 0x0B77u}, {0x0BF0u, 0x0BF2u}, {0x0C78u, 0x0C7Eu}, {0x0D58u, 0x0D5Eu},
    {0x0D70u, 0x0D78u}, {0x0F2Au, 0x0F33u}, {0x1369u, 0x137Cu}, {0x16EEu, 0x16F0u},
    {0x17F0u, 0x17F9u}, {0x19DAu, 0x19DAu}, {0x2070u, 0x2070u}, {0x2074u, 0x2079u},
    {0x2080u, 0x2089u}, {0x2150u, 0x2182u}, {0x2185u, 0x2189u}, {0x2460u, 0x249Bu},
    {0x24EAu, 0x24FFu}, {0x2776u, 0x2793u}, {0x2CFDu, 0x2CFDu}, {0x3007u, 0x3007u},
    {0x3021u, 0x3029u}, {0x3038u, 0x303Au}, {0x3192u, 0x3195u}, {0x3220u, 0x3229u},
    {0x3248u, 0x324Fu}, {0x3251u, 0x325Fu}, {0x3280u, 0x3289u}, {0x32B1u, 0x32BFu},
    {0xA6E6u, 0xA6EFu}
};

constexpr bool is_unicode_number(char16_t ch) noexcept
{
    if (is_nd_digit(ch)) return true;
    if (ch < 0x80u) return false;
    if (ch >= 0xA830u && ch <= 0xA835u) return true;
    return vn_in_ranges(ch, kUnicodeNlNoRanges, 33);
}

// Sorted BMP Sc (Currency_Symbol) ranges for \p{Sc}.
constexpr VnCharRange kCurrencySymbolRanges[17] = {
    {0x00A2u, 0x00A5u}, {0x058Fu, 0x058Fu}, {0x060Bu, 0x060Bu}, {0x07FEu, 0x07FFu},
    {0x09F2u, 0x09F3u}, {0x09FBu, 0x09FBu}, {0x0AF1u, 0x0AF1u}, {0x0BF9u, 0x0BF9u},
    {0x0E3Fu, 0x0E3Fu}, {0x17DBu, 0x17DBu}, {0x20A0u, 0x20C0u}, {0xA838u, 0xA838u},
    {0xFDFCu, 0xFDFCu}, {0xFE69u, 0xFE69u}, {0xFF04u, 0xFF04u}, {0xFFE0u, 0xFFE1u},
    {0xFFE5u, 0xFFE6u}
};

constexpr bool is_currency_symbol(char16_t ch) noexcept
{
    if (ch == u'$') return true;
    if (ch < 0x80u) return false;
    return vn_in_ranges(ch, kCurrencySymbolRanges, 17);
}

constexpr bool is_connector_punct(char16_t ch) noexcept
{
    return ch == u'_' || ch == 0x203Fu || ch == 0x2040u || ch == 0x2054u ||
           ch == 0xFE33u || ch == 0xFE34u ||
           (ch >= 0xFE4Du && ch <= 0xFE4Fu) || ch == 0xFF3Fu;
}

constexpr bool is_regex_word_char(char16_t ch) noexcept
{
    if (ch < 0x80u) return is_ascii_alnum(ch) || ch == u'_';
    return vn_is_letter_or_digit(ch) || vn_is_non_spacing_mark(ch) || is_connector_punct(ch);
}

constexpr bool is_word_boundary(std::u16string_view text, size_t index) noexcept
{
    const bool left_word = index > 0 && is_regex_word_char(text[index - 1]);
    const bool right_word = index < text.size() && is_regex_word_char(text[index]);
    return left_word != right_word;
}

// ============================================================================
// Minimal RFC 8259 UTF-16 JSON Reader (zero exceptions, strict validation)
// ============================================================================

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

bool scan_json_string(std::u16string_view src, size_t& pos, std::u16string* decoded_out = nullptr)
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

bool scan_json_number(std::u16string_view src, size_t& pos) noexcept
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

bool json_parse_value(std::u16string_view src, size_t& pos, JsonSpan& out, int depth = 0)
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

bool json_parse_document(std::u16string_view src, JsonSpan& out_root)
{
    size_t pos = 0;
    if (!json_parse_value(src, pos, out_root, 0)) return false;
    skip_json_ws(src, pos);
    return pos == src.size();
}

bool json_decode_string(JsonSpan val, std::u16string& out)
{
    if (val.kind != JsonKind::String) return false;
    size_t pos = 0;
    return scan_json_string(val.raw, pos, &out) && pos == val.raw.size();
}

bool json_try_get_property(JsonSpan obj, std::u16string_view target_key, JsonSpan& out_val)
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

bool json_try_get_double(JsonSpan val, double& out)
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

bool json_try_get_int64(JsonSpan val, int64_t& out) noexcept
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

bool json_try_get_int32(JsonSpan val, int32_t& out) noexcept
{
    int64_t v = 0;
    if (!json_try_get_int64(val, v)) return false;
    if (v < INT32_MIN || v > INT32_MAX) return false;
    out = static_cast<int32_t>(v);
    return true;
}

// ============================================================================
// 1. AiUsageParsing Helpers
// ============================================================================

constexpr int64_t kMinUnixTimeSeconds = -62135596800LL;
constexpr int64_t kMaxUnixTimeSeconds = 253402300799LL;

constexpr bool is_valid_bucket_id_view(std::u16string_view id) noexcept
{
    if (id.size() > 64) return false;
    for (char16_t c : id)
    {
        if (!is_ascii_alnum(c) && c != u'_' && c != u'-') return false;
    }
    return true;
}

bool parse_quota_window(
    JsonSpan value,
    std::u16string_view percent_key,
    std::u16string_view reset_key,
    uint8_t label_kind,
    bool has_duration,
    int32_t duration_minutes,
    bool has_bucket,
    std::u16string_view bucket_id,
    VnAiParsedWindow& out_win)
{
    JsonSpan p_span{};
    double percent = 0.0;
    if (!json_try_get_property(value, percent_key, p_span) ||
        !json_try_get_double(p_span, percent) ||
        percent < 0.0 || percent > 100.0)
    {
        return false;
    }

    bool has_reset = false;
    int64_t reset_seconds = 0;
    JsonSpan r_span{};
    if (json_try_get_property(value, reset_key, r_span) && r_span.kind != JsonKind::Null)
    {
        if (!json_try_get_int64(r_span, reset_seconds) ||
            reset_seconds < kMinUnixTimeSeconds ||
            reset_seconds > kMaxUnixTimeSeconds)
        {
            return false;
        }
        has_reset = true;
    }

    out_win = {};
    out_win.used_percent = percent;
    out_win.reset_unix_seconds = reset_seconds;
    out_win.duration_minutes = has_duration ? duration_minutes : 0;
    out_win.has_duration = has_duration ? 1 : 0;
    out_win.has_reset = has_reset ? 1 : 0;
    out_win.label_kind = label_kind;
    if (has_bucket && bucket_id.size() <= 64)
    {
        out_win.has_bucket = 1;
        out_win.bucket_len = static_cast<uint8_t>(bucket_id.size());
        for (size_t i = 0; i < bucket_id.size(); ++i)
        {
            out_win.bucket_ascii[i] = static_cast<uint8_t>(bucket_id[i]);
        }
    }
    return true;
}

void add_codex_bucket_slot(
    JsonSpan bucket,
    std::u16string_view slot_name,
    uint8_t label_kind,
    bool has_bucket,
    std::u16string_view bucket_id,
    std::vector<VnAiParsedWindow>& windows)
{
    JsonSpan value{};
    if (!json_try_get_property(bucket, slot_name, value) || value.kind != JsonKind::Object)
        return;

    bool has_duration = false;
    int32_t mins = 0;
    JsonSpan dur_span{};
    if (json_try_get_property(value, u"windowDurationMins", dur_span) &&
        json_try_get_int32(dur_span, mins) && mins > 0)
    {
        has_duration = true;
    }

    VnAiParsedWindow win{};
    if (parse_quota_window(value, u"usedPercent", u"resetsAt", label_kind,
                           has_duration, mins, has_bucket, bucket_id, win))
    {
        windows.push_back(win);
    }
}

void add_codex_bucket(JsonSpan bucket, std::u16string_view id, std::vector<VnAiParsedWindow>& windows)
{
    if (bucket.kind != JsonKind::Object) return;
    const bool valid_id = is_valid_bucket_id_view(id);
    add_codex_bucket_slot(bucket, u"primary", 0, valid_id, id, windows);
    add_codex_bucket_slot(bucket, u"secondary", 1, valid_id, id, windows);
}

// ============================================================================
// 2. TranslationTextIntegrity Fact Scanner & Normalizer
// ============================================================================

constexpr bool is_fact_trim_punct(char16_t ch) noexcept
{
    return ch == u'.' || ch == u',' || ch == u';' || ch == u':' ||
           ch == u')' || ch == u']' || ch == u'}' || ch == u'!' || ch == u'?';
}

constexpr bool is_url_body_char(char16_t ch) noexcept
{
    if (vnotch::is_unicode_whitespace(ch)) return false;
    if (ch == u'<' || ch == u'>' || ch == u'[' || ch == u']' ||
        ch == u'(' || ch == u')' || ch == u'*' || ch == u'`')
        return false;
    if (ch >= 0x3000u && ch <= 0x303Fu) return false;
    if (ch >= 0xFF01u && ch <= 0xFF65u) return false;
    return true;
}

constexpr bool is_sign_char(char16_t ch) noexcept
{
    return ch == u'+' || ch == u'-' || ch == 0x2212u;
}

size_t try_match_fact_at(std::u16string_view text, size_t pos, bool& out_is_entity) noexcept
{
    out_is_entity = false;
    const size_t n = text.size();
    const char16_t first = text[pos];

    // Branch 1: &(?:#[xX][0-9a-fA-F]+|#\d+|[A-Za-z][A-Za-z0-9]+);
    if (first == u'&' && pos + 3 < n)
    {
        size_t cur = pos + 1;
        if (text[cur] == u'#')
        {
            ++cur;
            if (cur < n && (text[cur] == u'x' || text[cur] == u'X'))
            {
                ++cur;
                const size_t hex_start = cur;
                while (cur < n && is_ascii_hex(text[cur])) ++cur;
                if (cur > hex_start && cur < n && text[cur] == u';')
                {
                    out_is_entity = true;
                    return (cur + 1) - pos;
                }
            }
            else
            {
                const size_t dec_start = cur;
                while (cur < n && is_nd_digit(text[cur])) ++cur;
                if (cur > dec_start && cur < n && text[cur] == u';')
                {
                    out_is_entity = true;
                    return (cur + 1) - pos;
                }
            }
        }
        else if (is_ascii_alpha(text[cur]))
        {
            ++cur;
            const size_t alnum_start = cur;
            while (cur < n && is_ascii_alnum(text[cur])) ++cur;
            if (cur > alnum_start && cur < n && text[cur] == u';')
            {
                out_is_entity = true;
                return (cur + 1) - pos;
            }
        }
    }

    // Branch 2: (?:https?://|www\.)[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]+
    if (first == u'h' || first == u'w')
    {
        size_t prefix_len = 0;
        if (text.substr(pos, 8) == u"https://") prefix_len = 8;
        else if (text.substr(pos, 7) == u"http://") prefix_len = 7;
        else if (text.substr(pos, 4) == u"www.") prefix_len = 4;

        if (prefix_len > 0)
        {
            size_t cur = pos + prefix_len;
            while (cur < n && is_url_body_char(text[cur])) ++cur;
            if (cur > pos + prefix_len) return cur - pos;
        }
    }

    // Branch 3: [\w.+-]+@[\w.-]+\.[\p{L}]{2,}
    if (is_regex_word_char(first) || first == u'.' || first == u'+' || first == u'-')
    {
        size_t cur = pos;
        while (cur < n && (is_regex_word_char(text[cur]) || text[cur] == u'.' || text[cur] == u'+' || text[cur] == u'-'))
            ++cur;
        if (cur > pos && cur < n && text[cur] == u'@')
        {
            const size_t dom_start = cur + 1;
            size_t max_k = dom_start;
            while (max_k < n && (is_regex_word_char(text[max_k]) || text[max_k] == u'.' || text[max_k] == u'-'))
                ++max_k;
            if (max_k >= dom_start + 4)
            {
                for (size_t dot = max_k - 2; dot > dom_start; --dot)
                {
                    if (text[dot] == u'.')
                    {
                        size_t tld_end = dot + 1;
                        while (tld_end < n && is_unicode_letter(text[tld_end])) ++tld_end;
                        if (tld_end - (dot + 1) >= 2)
                            return tld_end - pos;
                    }
                }
            }
        }
    }

    // Branch 4: (?:[\p{L}\p{N}-]+\.)+[A-Za-z]{2,}(?:/[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]*)?
    if (is_unicode_letter(first) || is_unicode_number(first) || first == u'-')
    {
        size_t after_dots_stack[64];
        size_t group_count = 0;
        size_t cur = pos;
        while (cur < n)
        {
            const size_t lbl_start = cur;
            while (cur < n && (is_unicode_letter(text[cur]) || is_unicode_number(text[cur]) || text[cur] == u'-'))
                ++cur;
            if (cur == lbl_start || cur >= n || text[cur] != u'.')
                break;
            ++cur;
            if (group_count < 64)
                after_dots_stack[group_count++] = cur;
            else
                after_dots_stack[63] = cur;
        }
        for (size_t k = group_count; k > 0; --k)
        {
            const size_t tld_start = after_dots_stack[k - 1];
            size_t tld_end = tld_start;
            while (tld_end < n && is_ascii_alpha(text[tld_end])) ++tld_end;
            if (tld_end - tld_start >= 2)
            {
                size_t end = tld_end;
                if (end < n && text[end] == u'/')
                {
                    ++end;
                    while (end < n && is_url_body_char(text[end])) ++end;
                }
                return end - pos;
            }
        }
    }

    // Branch 5: [A-Z]{2,}[-_]\d+[A-Z0-9_-]*
    if (first >= u'A' && first <= u'Z')
    {
        size_t cur = pos;
        while (cur < n && text[cur] >= u'A' && text[cur] <= u'Z') ++cur;
        if (cur - pos >= 2 && cur < n && (text[cur] == u'-' || text[cur] == u'_'))
        {
            ++cur;
            const size_t dig_start = cur;
            while (cur < n && is_nd_digit(text[cur])) ++cur;
            if (cur > dig_start)
            {
                while (cur < n && ((text[cur] >= u'A' && text[cur] <= u'Z') ||
                                   (text[cur] >= u'0' && text[cur] <= u'9') ||
                                   text[cur] == u'_' || text[cur] == u'-'))
                {
                    ++cur;
                }
                return cur - pos;
            }
        }
    }

    // Branch 6: (?:[+\-−]?\p{Sc}\s*)?[+\-−]?\d+(?:[.,:/\u066B\u066C-]\d+)*(?:\s*(?:[%\p{Sc}]|元|人民币|USD|VND|EUR|CNY|JPY|GBP))?
    if (is_sign_char(first) || is_currency_symbol(first) || is_nd_digit(first))
    {
        auto try_parse_number_core = [&](size_t start_idx, size_t& out_end) noexcept -> bool
        {
            size_t c = start_idx;
            if (c < n && is_sign_char(text[c])) ++c;
            const size_t d0 = c;
            while (c < n && is_nd_digit(text[c])) ++c;
            if (c == d0) return false;

            while (c + 1 < n)
            {
                const char16_t sep = text[c];
                if ((sep == u'.' || sep == u',' || sep == u':' || sep == u'/' ||
                     sep == 0x066Bu || sep == 0x066Cu || sep == u'-') &&
                    is_nd_digit(text[c + 1]))
                {
                    c += 2;
                    while (c < n && is_nd_digit(text[c])) ++c;
                }
                else
                {
                    break;
                }
            }

            size_t ws = c;
            while (ws < n && vnotch::is_unicode_whitespace(text[ws])) ++ws;
            if (ws < n)
            {
                const char16_t u = text[ws];
                if (u == u'%' || is_currency_symbol(u) || u == 0x5143u) // 元
                {
                    c = ws + 1;
                }
                else if (text.substr(ws, 3) == u"\x4EBA\x6C11\x5143" || // 人民币
                         text.substr(ws, 3) == u"USD" ||
                         text.substr(ws, 3) == u"VND" ||
                         text.substr(ws, 3) == u"EUR" ||
                         text.substr(ws, 3) == u"CNY" ||
                         text.substr(ws, 3) == u"JPY" ||
                         text.substr(ws, 3) == u"GBP")
                {
                    c = ws + 3;
                }
            }
            out_end = c;
            return true;
        };

        size_t p = pos;
        if (p < n && is_sign_char(text[p])) ++p;
        if (p < n && is_currency_symbol(text[p]))
        {
            ++p;
            while (p < n && vnotch::is_unicode_whitespace(text[p])) ++p;
            size_t end_with_sc = 0;
            if (try_parse_number_core(p, end_with_sc))
                return end_with_sc - pos;
        }

        size_t end_plain = 0;
        if (try_parse_number_core(pos, end_plain))
            return end_plain - pos;
    }

    return 0;
}

constexpr bool has_numeric_host_suffix(std::u16string_view val) noexcept
{
    for (size_t i = 0; i + 2 < val.size(); ++i)
    {
        if (val[i] == u'.' && is_ascii_alpha(val[i + 1]) && is_ascii_alpha(val[i + 2]))
            return true;
    }
    return false;
}

constexpr bool is_numeric_fact_match(std::u16string_view text, size_t match_idx, std::u16string_view match_val) noexcept
{
    if (match_val.empty()) return false;
    const char16_t first = match_val[0];
    if (!is_nd_digit(first) && !is_sign_char(first) && !is_currency_symbol(first))
        return false;
    if (match_val.find(u'@') != std::u16string_view::npos ||
        match_val.find(u"://") != std::u16string_view::npos)
        return false;
    if (match_val.find(u'.') != std::u16string_view::npos && has_numeric_host_suffix(match_val))
        return false;
    if (match_idx == 0) return true;
    const char16_t prev = text[match_idx - 1];
    return !(is_ascii_alnum(prev) || prev == u'_');
}

constexpr bool is_idiomatic_one_match(std::u16string_view text, size_t match_idx, std::u16string_view match_val) noexcept
{
    if (match_val != u"1") return false;
    if (match_idx > 0)
    {
        const char16_t prev = text[match_idx - 1];
        if (is_unicode_letter(prev) || is_unicode_number(prev) || is_currency_symbol(prev) ||
            prev == u'_' || prev == u'.' || prev == u',' || prev == u'/' || is_sign_char(prev))
        {
            return false;
        }
    }

    size_t cur = match_idx + 1;
    const size_t ws_start = cur;
    while (cur < text.size() && (text[cur] == u' ' || text[cur] == u'\t')) ++cur;
    if (cur == ws_start || cur >= text.size()) return false;

    size_t word_end = 0;
    if (cur + 2 <= text.size() && ascii_to_lower(text[cur]) == u't' &&
        (text[cur + 1] == 0x00EDu || text[cur + 1] == 0x00CDu ||
         text[cur + 1] == 0x00FDu || text[cur + 1] == 0x00DDu))
    {
        word_end = cur + 2; // tí / tý
    }
    else if (cur + 4 <= text.size() &&
             ascii_to_lower(text[cur]) == u'c' &&
             ascii_to_lower(text[cur + 1]) == u'h' &&
             (text[cur + 2] == 0x00FAu || text[cur + 2] == 0x00DAu) &&
             ascii_to_lower(text[cur + 3]) == u't')
    {
        word_end = cur + 4; // chút
    }
    else
    {
        return false;
    }

    return word_end == text.size() || !is_regex_word_char(text[word_end]);
}

void scan_all_facts(std::u16string_view text, std::vector<VnTextFactSpan>& out_spans)
{
    out_spans.clear();
    size_t pos = 0;
    while (pos < text.size())
    {
        bool is_entity = false;
        const size_t len = try_match_fact_at(text, pos, is_entity);
        if (len == 0)
        {
            ++pos;
            continue;
        }

        const std::u16string_view match_val = text.substr(pos, len);
        size_t trim_len = len;
        while (trim_len > 0 && is_fact_trim_punct(match_val[trim_len - 1]))
            --trim_len;

        VnTextFactSpan span{};
        span.offset = static_cast<int32_t>(pos);
        span.length = static_cast<int32_t>(len);
        span.trimmed_length = static_cast<int32_t>(trim_len);
        span.is_numeric = is_numeric_fact_match(text, pos, match_val) ? 1 : 0;
        span.is_idiomatic_one = is_idiomatic_one_match(text, pos, match_val) ? 1 : 0;
        span.is_entity = is_entity ? 1 : 0;
        out_spans.push_back(span);
        pos += len;
    }
}

std::u16string normalize_numeric_fact_local(std::u16string_view fact)
{
    auto is_word = [](char16_t c) noexcept
    {
        return vn_is_letter_or_digit(c) || c == u'_';
    };
    auto is_ascii_dig = [](char16_t c) noexcept
    {
        return c >= u'0' && c <= u'9';
    };

    std::u16string out;
    out.reserve(fact.size());
    for (size_t i = 0; i < fact.size(); ++i)
    {
        const char16_t c = fact[i];
        if (c == u'.' || c == u',')
        {
            size_t d_before = 0;
            while (d_before < 4 && i > d_before && is_ascii_dig(fact[i - 1 - d_before]))
                ++d_before;
            const bool valid_before = (d_before >= 1 && d_before <= 3) &&
                                      (i == d_before || !is_word(fact[i - 1 - d_before]));
            const bool valid_after = (i + 3 < fact.size()) &&
                                     is_ascii_dig(fact[i + 1]) &&
                                     is_ascii_dig(fact[i + 2]) &&
                                     is_ascii_dig(fact[i + 3]) &&
                                     (i + 4 == fact.size() || !is_ascii_dig(fact[i + 4]));
            if (valid_before && valid_after)
                continue;
        }
        out.push_back(c);
    }
    return out;
}

// ============================================================================
// 3. SelectedPrice Currency Helpers
// ============================================================================

constexpr std::u16string_view kIsoCurrencyCodes[54] = {
    u"AED", u"ARS", u"AUD", u"BDT", u"BGN", u"BHD", u"BRL", u"CAD", u"CHF", u"CLP",
    u"CNY", u"COP", u"CZK", u"DKK", u"EGP", u"EUR", u"GBP", u"HKD", u"HUF", u"IDR",
    u"ILS", u"INR", u"ISK", u"JPY", u"KRW", u"KWD", u"LAK", u"LKR", u"MAD", u"MMK",
    u"MXN", u"MYR", u"NOK", u"NPR", u"NZD", u"PEN", u"PHP", u"PKR", u"PLN", u"QAR",
    u"RON", u"RSD", u"RUB", u"SAR", u"SEK", u"SGD", u"THB", u"TRY", u"TWD", u"UAH",
    u"USD", u"UZS", u"VND", u"ZAR"
};

constexpr bool is_iso_currency_icase(std::u16string_view s) noexcept
{
    if (s.size() != 3) return false;
    const char16_t c0 = static_cast<char16_t>(s[0] & ~0x20u);
    const char16_t c1 = static_cast<char16_t>(s[1] & ~0x20u);
    const char16_t c2 = static_cast<char16_t>(s[2] & ~0x20u);
    if (c0 < u'A' || c0 > u'Z' || c1 < u'A' || c1 > u'Z' || c2 < u'A' || c2 > u'Z')
        return false;
    const char16_t upper[3] = {c0, c1, c2};
    const std::u16string_view up(upper, 3);
    for (std::u16string_view code : kIsoCurrencyCodes)
    {
        if (code == up) return true;
    }
    return false;
}

constexpr bool is_price_single_symbol(char16_t ch) noexcept
{
    return ch == u'$' || ch == 0x20ACu || ch == 0x00A3u || ch == 0x00A5u ||
           ch == 0xFFE5u || ch == 0x20ABu || ch == 0x0111u || ch == 0x0110u ||
           ch == 0x20A9u || ch == 0x20B9u || ch == 0x0E3Fu || ch == 0x20BDu;
}

size_t match_qualified_symbol_token(std::u16string_view s, size_t pos) noexcept
{
    const size_t rem = s.size() - pos;
    if (rem >= 3)
    {
        const std::u16string_view sub3 = s.substr(pos, 3);
        if (equals_ascii_icase(sub3, u"US$") ||
            equals_ascii_icase(sub3, u"CA$") ||
            equals_ascii_icase(sub3, u"AU$") ||
            equals_ascii_icase(sub3, u"NZ$") ||
            equals_ascii_icase(sub3, u"HK$"))
        {
            return 3;
        }
        if ((ascii_to_lower(sub3[0]) == u'c' && ascii_to_lower(sub3[1]) == u'n' && sub3[2] == 0x00A5u) ||
            (ascii_to_lower(sub3[0]) == u'j' && ascii_to_lower(sub3[1]) == u'p' && sub3[2] == 0x00A5u))
        {
            return 3;
        }
    }
    if (rem >= 2)
    {
        const std::u16string_view sub2 = s.substr(pos, 2);
        if (equals_ascii_icase(sub2, u"S$") || equals_ascii_icase(sub2, u"R$"))
            return 2;
    }
    return 0;
}

constexpr bool is_price_amount_tail(char16_t ch) noexcept
{
    return (ch >= u'0' && ch <= u'9') ||
           ch == u'.' || ch == u',' || ch == u'\'' ||
           ch == 0x00A0u || ch == 0x202Fu || ch == u' ';
}

size_t match_price_affix(std::u16string_view s, size_t pos) noexcept
{
    if (pos >= s.size()) return 0;
    const size_t qual_len = match_qualified_symbol_token(s, pos);
    if (qual_len > 0) return qual_len;

    if (is_unicode_letter(s[pos]))
    {
        size_t cur = pos;
        while (cur < s.size() && is_unicode_letter(s[cur])) ++cur;
        const size_t after_word1 = cur;
        size_t ws = cur;
        while (ws < s.size() && vnotch::is_unicode_whitespace(s[ws])) ++ws;
        if (ws > after_word1 && ws < s.size() && is_unicode_letter(s[ws]))
        {
            size_t cur2 = ws;
            while (cur2 < s.size() && is_unicode_letter(s[cur2])) ++cur2;
            return cur2 - pos;
        }
        return after_word1 - pos;
    }

    if (is_price_single_symbol(s[pos])) return 1;
    return 0;
}

std::u16string resolve_currency_token_helper(
    std::u16string_view token,
    std::u16string_view source_lang,
    std::u16string_view local_dollar) noexcept
{
    const std::u16string_view trimmed = vnotch::trim_view(token);
    const std::u16string_view ld = (trimmed == u"$") ? local_dollar : std::u16string_view{};
    char16_t buf[8]{};
    int32_t len = 0;
    if (vn_currency_resolve_token(
            trimmed.data(), static_cast<int32_t>(trimmed.size()),
            source_lang.data(), static_cast<int32_t>(source_lang.size()),
            ld.data(), static_cast<int32_t>(ld.size()),
            buf, 8, &len) == VN_OK && len > 0)
    {
        return std::u16string(buf, static_cast<size_t>(len));
    }
    return {};
}

constexpr bool is_dollar_qualified_iso(std::u16string_view code) noexcept
{
    return code == u"USD" || code == u"CAD" || code == u"AUD" ||
           code == u"NZD" || code == u"HKD" || code == u"SGD";
}

// ============================================================================
// 4. ClipboardClassifier Helpers (Email & Code Heuristics)
// ============================================================================

bool contains_clipboard_email(std::u16string_view text) noexcept
{
    if (text.find(u'@') == std::u16string_view::npos) return false;
    const size_t n = text.size();
    for (size_t pos = 0; pos < n; ++pos)
    {
        const char16_t ch = text[pos];
        if (!(is_regex_word_char(ch) || ch == u'.' || ch == u'+' || ch == u'-'))
            continue;
        if (!is_word_boundary(text, pos))
            continue;

        size_t cur = pos;
        while (cur < n && (is_regex_word_char(text[cur]) || text[cur] == u'.' || text[cur] == u'+' || text[cur] == u'-'))
            ++cur;
        if (cur == pos || cur >= n || text[cur] != u'@')
            continue;

        const size_t dom_start = cur + 1;
        size_t max_k = dom_start;
        while (max_k < n && (is_regex_word_char(text[max_k]) || text[max_k] == u'.' || text[max_k] == u'-'))
            ++max_k;
        if (max_k < dom_start + 4)
            continue;

        for (size_t dot = max_k - 2; dot > dom_start; --dot)
        {
            if (text[dot] == u'.')
            {
                size_t tld_end = dot + 1;
                while (tld_end < n && is_ascii_alpha(text[tld_end])) ++tld_end;
                if (tld_end - (dot + 1) >= 2 && is_word_boundary(text, tld_end))
                    return true;
            }
        }
    }
    return false;
}

bool match_icase_kw(std::u16string_view text, size_t pos, std::u16string_view kw) noexcept
{
    return pos + kw.size() <= text.size() && equals_ascii_icase(text.substr(pos, kw.size()), kw);
}

bool looks_like_clipboard_code(std::u16string_view text) noexcept
{
    const size_t n = text.size();
    if (n < 3) return false;

    // Unanchored branch: =>\s*\{
    for (size_t i = 0; i + 2 < n; ++i)
    {
        if (text[i] == u'=' && text[i + 1] == u'>')
        {
            size_t ws = i + 2;
            while (ws < n && vnotch::is_unicode_whitespace(text[ws])) ++ws;
            if (ws < n && text[ws] == u'{') return true;
        }
    }

    // Multiline line-anchored branches: (?m)^[ \t]*(...)
    size_t line_start = 0;
    while (line_start <= n)
    {
        size_t p = line_start;
        while (p < n && (text[p] == u' ' || text[p] == u'\t')) ++p;
        if (p < n)
        {
            // 1. using\s+[\w.]+;
            if (match_icase_kw(text, p, u"using") && p + 5 < n && vnotch::is_unicode_whitespace(text[p + 5]))
            {
                size_t q = p + 5;
                while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                const size_t ns_start = q;
                while (q < n && (is_regex_word_char(text[q]) || text[q] == u'.')) ++q;
                if (q > ns_start && q < n && text[q] == u';') return true;
            }

            // 2. import\s
            if (match_icase_kw(text, p, u"import") && p + 6 < n && vnotch::is_unicode_whitespace(text[p + 6]))
                return true;

            // 3. from\s+\w+\s+import
            if (match_icase_kw(text, p, u"from") && p + 4 < n && vnotch::is_unicode_whitespace(text[p + 4]))
            {
                size_t q = p + 4;
                while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                const size_t mod_start = q;
                while (q < n && is_regex_word_char(text[q])) ++q;
                if (q > mod_start && q < n && vnotch::is_unicode_whitespace(text[q]))
                {
                    while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                    if (match_icase_kw(text, q, u"import")) return true;
                }
            }

            // 4. (?:public|private|internal)\s+(?:static\s+)?(?:class|void|Task)\b
            {
                size_t vis_len = 0;
                if (match_icase_kw(text, p, u"public")) vis_len = 6;
                else if (match_icase_kw(text, p, u"private")) vis_len = 7;
                else if (match_icase_kw(text, p, u"internal")) vis_len = 8;

                if (vis_len > 0 && p + vis_len < n && vnotch::is_unicode_whitespace(text[p + vis_len]))
                {
                    size_t q = p + vis_len;
                    while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                    if (match_icase_kw(text, q, u"static") && q + 6 < n && vnotch::is_unicode_whitespace(text[q + 6]))
                    {
                        size_t after_static = q + 6;
                        while (after_static < n && vnotch::is_unicode_whitespace(text[after_static])) ++after_static;
                        q = after_static;
                    }
                    size_t kw_len = 0;
                    if (match_icase_kw(text, q, u"class")) kw_len = 5;
                    else if (match_icase_kw(text, q, u"void")) kw_len = 4;
                    else if (match_icase_kw(text, q, u"task")) kw_len = 4;
                    if (kw_len > 0 && is_word_boundary(text, q + kw_len)) return true;
                }
            }

            // 5. (?:const|let|var|int|long|float|double|decimal|bool|string|char|byte)\s+\w+\s*=
            {
                static constexpr std::u16string_view kDeclKeywords[] = {
                    u"decimal", u"double", u"string", u"const", u"float",
                    u"long", u"bool", u"char", u"byte", u"let", u"var", u"int"
                };
                for (std::u16string_view kw : kDeclKeywords)
                {
                    if (match_icase_kw(text, p, kw) && p + kw.size() < n &&
                        vnotch::is_unicode_whitespace(text[p + kw.size()]))
                    {
                        size_t q = p + kw.size();
                        while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                        const size_t id_start = q;
                        while (q < n && is_regex_word_char(text[q])) ++q;
                        if (q > id_start)
                        {
                            while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                            if (q < n && text[q] == u'=') return true;
                        }
                        break;
                    }
                }
            }

            // 6 & 7. def\s+\w+\( | function\s+\w+\(
            {
                size_t fn_len = 0;
                if (match_icase_kw(text, p, u"def")) fn_len = 3;
                else if (match_icase_kw(text, p, u"function")) fn_len = 8;
                if (fn_len > 0 && p + fn_len < n && vnotch::is_unicode_whitespace(text[p + fn_len]))
                {
                    size_t q = p + fn_len;
                    while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                    const size_t id_start = q;
                    while (q < n && is_regex_word_char(text[q])) ++q;
                    if (q > id_start && q < n && text[q] == u'(') return true;
                }
            }

            // 8. SELECT\s+.+\s+FROM\b
            if (match_icase_kw(text, p, u"select") && p + 6 < n && vnotch::is_unicode_whitespace(text[p + 6]))
            {
                size_t q = p + 6;
                while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                if (q < n && text[q] != u'\n')
                {
                    size_t dot_end = q + 1;
                    while (dot_end <= n)
                    {
                        if (dot_end < n && vnotch::is_unicode_whitespace(text[dot_end]))
                        {
                            size_t ws_end = dot_end;
                            while (ws_end < n && vnotch::is_unicode_whitespace(text[ws_end])) ++ws_end;
                            if (match_icase_kw(text, ws_end, u"from") && is_word_boundary(text, ws_end + 4))
                                return true;
                        }
                        if (dot_end == n || text[dot_end] == u'\n') break;
                        ++dot_end;
                    }
                }
            }

            // 9. <\/?(?:div|html|script|style|body)\b
            if (text[p] == u'<')
            {
                size_t q = p + 1;
                if (q < n && text[q] == u'/') ++q;
                static constexpr std::u16string_view kHtmlTags[] = {
                    u"script", u"style", u"html", u"body", u"div"
                };
                for (std::u16string_view tag : kHtmlTags)
                {
                    if (match_icase_kw(text, q, tag) && is_word_boundary(text, q + tag.size()))
                        return true;
                }
            }

            // 10. (?:\w+\.)*\w+\([^\r\n]*\);[ \t]*$
            if (is_regex_word_char(text[p]))
            {
                size_t q = p;
                while (true)
                {
                    const size_t seg_start = q;
                    while (q < n && is_regex_word_char(text[q])) ++q;
                    if (q == seg_start) break;
                    if (q < n && text[q] == u'.')
                    {
                        ++q;
                        continue;
                    }
                    if (q < n && text[q] == u'(')
                    {
                        size_t line_end = q + 1;
                        bool has_cr = false;
                        while (line_end < n && text[line_end] != u'\n')
                        {
                            if (text[line_end] == u'\r') has_cr = true;
                            ++line_end;
                        }
                        if (!has_cr)
                        {
                            size_t trim_end = line_end;
                            while (trim_end > q + 1 && (text[trim_end - 1] == u' ' || text[trim_end - 1] == u'\t'))
                                --trim_end;
                            if (trim_end >= q + 3 && text[trim_end - 2] == u')' && text[trim_end - 1] == u';')
                                return true;
                        }
                    }
                    break;
                }
            }
        }

        const size_t nl = text.find(u'\n', line_start);
        if (nl == std::u16string_view::npos) break;
        line_start = nl + 1;
    }

    return false;
}

} // namespace

// ============================================================================
// Exported C ABI Implementations
// ============================================================================

VN_EXPORT int32_t __stdcall vn_ai_usage_is_valid_bucket_id(
    const wchar_t* id_ptr, int32_t id_len, uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr || id_len < 0 || (id_len > 0 && id_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_valid = is_valid_bucket_id_view(make_u16_view(id_ptr, id_len)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_ai_usage_parse_codex(
    const wchar_t* json_ptr, int32_t json_len,
    VnAiParsedWindow* out_windows, int32_t window_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || json_len < 0) return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        std::vector<VnAiParsedWindow> windows;
        windows.reserve(8);

        JsonSpan buckets{};
        if (json_try_get_property(root, u"rateLimitsByLimitId", buckets) && buckets.kind == JsonKind::Object)
        {
            struct BucketEntry
            {
                std::u16string name;
                JsonSpan value;
            };
            std::vector<BucketEntry> entries;
            entries.reserve(16);

            size_t pos = 1;
            skip_json_ws(buckets.raw, pos);
            if (pos < buckets.raw.size() && buckets.raw[pos] != u'}')
            {
                int32_t count = 0;
                while (pos < buckets.raw.size() && count < 16)
                {
                    skip_json_ws(buckets.raw, pos);
                    std::u16string key;
                    if (!scan_json_string(buckets.raw, pos, &key)) break;
                    skip_json_ws(buckets.raw, pos);
                    if (pos >= buckets.raw.size() || buckets.raw[pos] != u':') break;
                    ++pos;
                    JsonSpan val{};
                    if (!json_parse_value(buckets.raw, pos, val, 1)) break;
                    entries.push_back({std::move(key), val});
                    ++count;
                    skip_json_ws(buckets.raw, pos);
                    if (pos >= buckets.raw.size() || buckets.raw[pos] == u'}') break;
                    if (buckets.raw[pos] != u',') break;
                    ++pos;
                }
            }

            for (const auto& entry : entries)
            {
                if (entry.name == u"codex")
                    add_codex_bucket(entry.value, entry.name, windows);
            }
            for (const auto& entry : entries)
            {
                if (entry.name != u"codex")
                    add_codex_bucket(entry.value, entry.name, windows);
            }
        }

        if (windows.empty())
        {
            JsonSpan legacy{};
            if (json_try_get_property(root, u"rateLimits", legacy))
                add_codex_bucket(legacy, u"codex", windows);
        }

        const int32_t total = static_cast<int32_t>(windows.size());
        *out_count = total;
        if (total == 0) return VN_OK;
        if (out_windows == nullptr || window_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_windows, windows.data(), static_cast<size_t>(total) * sizeof(VnAiParsedWindow));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_ai_usage_parse_claude(
    const wchar_t* json_ptr, int32_t json_len,
    VnAiParsedWindow* out_windows, int32_t window_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || json_len < 0) return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        VnAiParsedWindow parsed[2]{};
        int32_t count = 0;

        JsonSpan limits{};
        if (json_try_get_property(root, u"rate_limits", limits) && limits.kind == JsonKind::Object)
        {
            JsonSpan five_hour{};
            if (json_try_get_property(limits, u"five_hour", five_hour) && five_hour.kind == JsonKind::Object)
            {
                if (parse_quota_window(five_hour, u"used_percentage", u"resets_at", 2, true, 300, false, {}, parsed[count]))
                    ++count;
            }

            JsonSpan seven_day{};
            if (json_try_get_property(limits, u"seven_day", seven_day) && seven_day.kind == JsonKind::Object)
            {
                if (parse_quota_window(seven_day, u"used_percentage", u"resets_at", 3, true, 10080, false, {}, parsed[count]))
                    ++count;
            }
        }

        *out_count = count;
        if (count == 0) return VN_OK;
        if (out_windows == nullptr || window_cap < count) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_windows, parsed, static_cast<size_t>(count) * sizeof(VnAiParsedWindow));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_claude_bridge_build_command(
    const wchar_t* script_ptr, int32_t script_len,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (out_len == nullptr || script_len < 0 || (script_len > 0 && script_ptr == nullptr))
        return VN_ERR_INVALID_ARG;

    constexpr std::u16string_view kCmdPrefix =
        u"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
    constexpr char kBase64Alphabet[65] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    const std::u16string_view script = make_u16_view(script_ptr, script_len);
    size_t invoke_u16_len = 4; // "& '" + "'"
    for (char16_t ch : script)
    {
        invoke_u16_len += (ch == u'\'') ? 2u : 1u;
    }

    const size_t byte_len = invoke_u16_len * 2u;
    const size_t b64_len = ((byte_len + 2u) / 3u) * 4u;
    const size_t total_len = kCmdPrefix.size() + b64_len;
    if (total_len > static_cast<size_t>(INT32_MAX)) return VN_ERR_FAIL;

    *out_len = static_cast<int32_t>(total_len);
    if (out_buf == nullptr || out_cap < static_cast<int32_t>(total_len))
        return VN_ERR_BUFFER_TOO_SMALL;

    std::memcpy(out_buf, kCmdPrefix.data(), kCmdPrefix.size() * sizeof(wchar_t));
    size_t write_idx = kCmdPrefix.size();

    uint8_t triad[3]{};
    int triad_count = 0;
    auto push_byte = [&](uint8_t b) noexcept
    {
        triad[triad_count++] = b;
        if (triad_count == 3)
        {
            const uint32_t v = (static_cast<uint32_t>(triad[0]) << 16) |
                               (static_cast<uint32_t>(triad[1]) << 8) |
                               static_cast<uint32_t>(triad[2]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 18) & 0x3Fu]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 12) & 0x3Fu]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 6) & 0x3Fu]);
            out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[v & 0x3Fu]);
            triad_count = 0;
        }
    };
    auto push_u16_le = [&](char16_t ch) noexcept
    {
        push_byte(static_cast<uint8_t>(ch & 0xFFu));
        push_byte(static_cast<uint8_t>((ch >> 8) & 0xFFu));
    };

    push_u16_le(u'&');
    push_u16_le(u' ');
    push_u16_le(u'\'');
    for (char16_t ch : script)
    {
        if (ch == u'\'')
        {
            push_u16_le(u'\'');
            push_u16_le(u'\'');
        }
        else
        {
            push_u16_le(ch);
        }
    }
    push_u16_le(u'\'');

    if (triad_count == 1)
    {
        const uint32_t v = static_cast<uint32_t>(triad[0]) << 16;
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 18) & 0x3Fu]);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 12) & 0x3Fu]);
        out_buf[write_idx++] = L'=';
        out_buf[write_idx++] = L'=';
    }
    else if (triad_count == 2)
    {
        const uint32_t v = (static_cast<uint32_t>(triad[0]) << 16) |
                           (static_cast<uint32_t>(triad[1]) << 8);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 18) & 0x3Fu]);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 12) & 0x3Fu]);
        out_buf[write_idx++] = static_cast<wchar_t>(kBase64Alphabet[(v >> 6) & 0x3Fu]);
        out_buf[write_idx++] = L'=';
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_translation_integrity_extract_facts(
    const wchar_t* text_ptr, int32_t text_len,
    VnTextFactSpan* out_spans, int32_t span_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
            return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);
        if (text.empty()) return VN_OK;

        std::vector<VnTextFactSpan> spans;
        scan_all_facts(text, spans);
        const int32_t total = static_cast<int32_t>(spans.size());
        *out_count = total;
        if (total == 0) return VN_OK;
        if (out_spans == nullptr || span_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_spans, spans.data(), static_cast<size_t>(total) * sizeof(VnTextFactSpan));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_integrity_inspect(
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* translated_ptr, int32_t translated_len,
    uint8_t* out_literals_preserved, uint8_t* out_protected_preserved) noexcept
{
    try
    {
        if (out_literals_preserved == nullptr || out_protected_preserved == nullptr ||
            source_len < 0 || translated_len < 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_literals_preserved = 0;
        *out_protected_preserved = 0;

        auto collect = [](std::u16string_view text,
                          std::vector<std::u16string>& observable,
                          std::vector<std::u16string>& prot)
        {
            std::vector<VnTextFactSpan> spans;
            scan_all_facts(text, spans);
            observable.reserve(spans.size());
            prot.reserve(spans.size());
            for (const VnTextFactSpan& s : spans)
            {
                const std::u16string_view val = text.substr(
                    static_cast<size_t>(s.offset), static_cast<size_t>(s.trimmed_length));
                if (s.is_idiomatic_one == 0)
                {
                    observable.push_back(s.is_numeric != 0 ? normalize_numeric_fact_local(val) : std::u16string(val));
                }
                if (s.is_numeric == 0)
                {
                    prot.push_back(std::u16string(val));
                }
            }
            std::sort(observable.begin(), observable.end());
            std::sort(prot.begin(), prot.end());
        };

        std::vector<std::u16string> src_obs, src_prot;
        std::vector<std::u16string> dst_obs, dst_prot;
        collect(make_u16_view(source_ptr, source_len), src_obs, src_prot);
        collect(make_u16_view(translated_ptr, translated_len), dst_obs, dst_prot);

        *out_literals_preserved = (src_obs == dst_obs) ? 1 : 0;
        *out_protected_preserved = (src_prot == dst_prot) ? 1 : 0;
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_qwen_translation_try_read_output(
    const wchar_t* json_ptr, int32_t json_len,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len, uint8_t* out_ok) noexcept
{
    try
    {
        if (out_len == nullptr || out_ok == nullptr || json_len < 0)
            return VN_ERR_INVALID_ARG;
        *out_len = 0;
        *out_ok = 0;

        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        size_t pos = 1;
        skip_json_ws(root.raw, pos);
        if (pos >= root.raw.size() || root.raw[pos] == u'}')
            return VN_OK;

        std::u16string key;
        if (!scan_json_string(root.raw, pos, &key) || key != u"translation")
            return VN_OK;
        skip_json_ws(root.raw, pos);
        if (pos >= root.raw.size() || root.raw[pos] != u':')
            return VN_OK;
        ++pos;

        JsonSpan val{};
        if (!json_parse_value(root.raw, pos, val, 1) || val.kind != JsonKind::String)
            return VN_OK;
        skip_json_ws(root.raw, pos);
        if (pos >= root.raw.size() || root.raw[pos] != u'}')
            return VN_OK;

        std::u16string decoded;
        if (!json_decode_string(val, decoded))
            return VN_OK;

        const std::u16string_view trimmed = vnotch::trim_view(decoded);
        *out_ok = 1;
        return write_wstr_out(trimmed, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_lang_split_passthrough_sentences(
    const wchar_t* text_ptr, int32_t text_len,
    VnSubTextSlice* out_slices, int32_t slice_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
            return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);

        auto is_hard_break = [](char16_t c) noexcept
        {
            return c == u'\r' || c == u'\n' || c == 0x3002u || c == 0xFF01u || c == 0xFF1Fu;
        };
        auto is_ascii_sent_end = [](char16_t c) noexcept
        {
            return c == u'.' || c == u'!' || c == u'?';
        };

        std::vector<VnSubTextSlice> slices;
        slices.reserve(8);
        size_t seg_start = 0;
        size_t i = 0;
        while (i < text.size())
        {
            if (is_hard_break(text[i]))
            {
                slices.push_back({static_cast<int32_t>(seg_start), static_cast<int32_t>(i - seg_start)});
                while (i < text.size() && is_hard_break(text[i])) ++i;
                seg_start = i;
            }
            else if (i > 0 && is_ascii_sent_end(text[i - 1]) && vnotch::is_unicode_whitespace(text[i]))
            {
                slices.push_back({static_cast<int32_t>(seg_start), static_cast<int32_t>(i - seg_start)});
                while (i < text.size() && vnotch::is_unicode_whitespace(text[i])) ++i;
                seg_start = i;
            }
            else
            {
                ++i;
            }
        }
        slices.push_back({static_cast<int32_t>(seg_start), static_cast<int32_t>(text.size() - seg_start)});

        const int32_t total = static_cast<int32_t>(slices.size());
        *out_count = total;
        if (out_slices == nullptr || slice_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_slices, slices.data(), static_cast<size_t>(total) * sizeof(VnSubTextSlice));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_structured_find_shortcodes(
    const wchar_t* text_ptr, int32_t text_len,
    VnSubTextSlice* out_slices, int32_t slice_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
            return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);

        std::vector<VnSubTextSlice> slices;
        for (size_t i = 0; i + 2 < text.size();)
        {
            if (text[i] == u':' && (is_ascii_alpha(text[i + 1]) || text[i + 1] == u'_'))
            {
                size_t j = i + 2;
                while (j < text.size() && (is_ascii_alnum(text[j]) || text[j] == u'_' || text[j] == u'+' || text[j] == u'-'))
                    ++j;
                if (j < text.size() && text[j] == u':')
                {
                    slices.push_back({static_cast<int32_t>(i), static_cast<int32_t>((j + 1) - i)});
                    i = j + 1;
                    continue;
                }
            }
            ++i;
        }

        const int32_t total = static_cast<int32_t>(slices.size());
        *out_count = total;
        if (total == 0) return VN_OK;
        if (out_slices == nullptr || slice_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_slices, slices.data(), static_cast<size_t>(total) * sizeof(VnSubTextSlice));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_structured_has_letter_outside_shortcodes(
    const wchar_t* text_ptr, int32_t text_len, uint8_t* out_has_letter) noexcept
{
    if (out_has_letter == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_has_letter = 0;
    const std::u16string_view text = make_u16_view(text_ptr, text_len);

    for (size_t i = 0; i < text.size();)
    {
        if (text[i] == u':' && i + 2 < text.size() && (is_ascii_alpha(text[i + 1]) || text[i + 1] == u'_'))
        {
            size_t j = i + 2;
            while (j < text.size() && (is_ascii_alnum(text[j]) || text[j] == u'_' || text[j] == u'+' || text[j] == u'-'))
                ++j;
            if (j < text.size() && text[j] == u':')
            {
                i = j + 1;
                continue;
            }
        }
        if (is_unicode_letter(text[i]))
        {
            *out_has_letter = 1;
            return VN_OK;
        }
        ++i;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_selected_price_has_conflicting_currency(
    const wchar_t* text_ptr, int32_t text_len,
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* local_dollar_ptr, int32_t local_dollar_len,
    uint8_t* out_conflicting) noexcept
{
    try
    {
        if (out_conflicting == nullptr || text_len < 0 || source_len < 0 || local_dollar_len < 0)
            return VN_ERR_INVALID_ARG;
        *out_conflicting = 0;

        const std::u16string_view text = make_u16_view(text_ptr, text_len);
        const std::u16string_view source = make_u16_view(source_ptr, source_len);
        const std::u16string_view local_dollar = make_u16_view(local_dollar_ptr, local_dollar_len);
        const size_t n = text.size();

        for (size_t pos = 0; pos < n;)
        {
            if (pos > 0 && (is_unicode_letter(text[pos - 1]) || is_unicode_number(text[pos - 1])))
            {
                ++pos;
                continue;
            }

            size_t pfx_len = match_qualified_symbol_token(text, pos);
            if (pfx_len == 0 && pos + 3 <= n && is_iso_currency_icase(text.substr(pos, 3)))
                pfx_len = 3;
            if (pfx_len == 0 && is_price_single_symbol(text[pos]))
                pfx_len = 1;
            if (pfx_len == 0)
            {
                ++pos;
                continue;
            }

            size_t cur = pos + pfx_len;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
            if (cur >= n || text[cur] < u'0' || text[cur] > u'9')
            {
                ++pos;
                continue;
            }
            ++cur;
            while (cur < n && is_price_amount_tail(text[cur])) ++cur;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;

            size_t sfx_len = 0;
            if (cur + 3 <= n && is_iso_currency_icase(text.substr(cur, 3)))
                sfx_len = 3;
            else if (cur < n && is_price_single_symbol(text[cur]))
                sfx_len = 1;

            const size_t match_end = cur + sfx_len;
            if (sfx_len == 0 ||
                (match_end < n && (is_unicode_letter(text[match_end]) || is_unicode_number(text[match_end]))))
            {
                ++pos;
                continue;
            }

            const std::u16string_view prefix = text.substr(pos, pfx_len);
            const std::u16string_view suffix = text.substr(cur, sfx_len);
            const std::u16string left = resolve_currency_token_helper(prefix, source, local_dollar);
            const std::u16string right = resolve_currency_token_helper(suffix, source, local_dollar);

            const bool qualified_dollar = (prefix == u"$" && is_dollar_qualified_iso(right));
            const bool qualified_yen = ((prefix == u"\x00A5" || prefix == u"\xFFE5") &&
                                        (right == u"JPY" || right == u"CNY"));
            if (!qualified_dollar && !qualified_yen && !left.empty() && !right.empty() && left != right)
            {
                *out_conflicting = 1;
                return VN_OK;
            }

            pos = match_end;
        }
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_selected_price_try_parse(
    const wchar_t* text_ptr, int32_t text_len,
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* local_dollar_ptr, int32_t local_dollar_len,
    const wchar_t* override_ptr, int32_t override_len,
    wchar_t* out_amount_buf, int32_t amount_cap, int32_t* out_amount_len,
    wchar_t* out_currency_buf, int32_t currency_cap, int32_t* out_currency_len,
    uint8_t* out_assumed_currency, uint8_t* out_ok) noexcept
{
    try
    {
        if (out_amount_len == nullptr || out_currency_len == nullptr ||
            out_assumed_currency == nullptr || out_ok == nullptr ||
            text_len < 0 || source_len < 0 || local_dollar_len < 0 || override_len < 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_amount_len = 0;
        *out_currency_len = 0;
        *out_assumed_currency = 0;
        *out_ok = 0;

        if (text_len == 0 || text_len > 128) return VN_OK;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);
        const std::u16string_view source = make_u16_view(source_ptr, source_len);
        const std::u16string_view local_dollar = make_u16_view(local_dollar_ptr, local_dollar_len);
        const std::u16string_view override_code = make_u16_view(override_ptr, override_len);
        const size_t n = text.size();

        size_t cur = 0;
        while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        if (cur >= n) return VN_OK;

        std::u16string_view prefix{};
        const size_t pfx_len = match_price_affix(text, cur);
        if (pfx_len > 0)
        {
            prefix = vnotch::trim_view(text.substr(cur, pfx_len));
            cur += pfx_len;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        }

        if (cur >= n || text[cur] < u'0' || text[cur] > u'9') return VN_OK;
        const size_t amt_start = cur;
        ++cur;
        while (cur < n && is_price_amount_tail(text[cur])) ++cur;
        const std::u16string_view amount_raw = vnotch::trim_view(text.substr(amt_start, cur - amt_start));

        while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        std::u16string_view suffix{};
        if (cur < n)
        {
            const size_t sfx_len = match_price_affix(text, cur);
            if (sfx_len == 0) return VN_OK;
            suffix = vnotch::trim_view(text.substr(cur, sfx_len));
            cur += sfx_len;
            while (cur < n && vnotch::is_unicode_whitespace(text[cur])) ++cur;
        }
        if (cur != n) return VN_OK;
        if (prefix.empty() && suffix.empty()) return VN_OK;

        std::u16string currency = resolve_currency_token_helper(
            !prefix.empty() ? prefix : suffix, source, local_dollar);
        const std::u16string explicit_currency = (suffix.size() == 3)
            ? resolve_currency_token_helper(suffix, source, local_dollar)
            : std::u16string{};

        const bool qualified_symbol =
            (prefix == u"$" && is_dollar_qualified_iso(explicit_currency)) ||
            ((prefix == u"\x00A5" || prefix == u"\xFFE5") &&
             (explicit_currency == u"JPY" || explicit_currency == u"CNY"));

        if (qualified_symbol) currency = explicit_currency;
        if (!qualified_symbol && !prefix.empty() && !suffix.empty() &&
            resolve_currency_token_helper(suffix, source, local_dollar) != currency)
        {
            return VN_OK;
        }

        uint8_t override_supported = 0;
        if (!override_code.empty())
        {
            vn_currency_supported(
                override_code.data(), static_cast<int32_t>(override_code.size()), &override_supported);
        }
        const bool overridden = override_supported != 0;

        const bool bare_ambiguous_symbol =
            prefix == u"$" || suffix == u"$" ||
            prefix == u"\x00A5" || prefix == u"\xFFE5" ||
            suffix == u"\x00A5" || suffix == u"\xFFE5";

        if (currency.empty() && !bare_ambiguous_symbol) return VN_OK;
        if (overridden) currency.assign(override_code.data(), override_code.size());
        if (currency.empty()) return VN_OK;

        char16_t norm_buf[140]{};
        int32_t norm_len = 0;
        uint8_t norm_valid = 0;
        if (vn_currency_normalize_amount(
                amount_raw.data(), static_cast<int32_t>(amount_raw.size()),
                currency.data(), static_cast<int32_t>(currency.size()),
                norm_buf, 140, &norm_len, &norm_valid) != VN_OK ||
            norm_valid == 0 || norm_len <= 0)
        {
            return VN_OK;
        }

        *out_amount_len = norm_len;
        *out_currency_len = static_cast<int32_t>(currency.size());
        *out_assumed_currency = (!overridden && !qualified_symbol && bare_ambiguous_symbol) ? 1 : 0;

        if (out_amount_buf == nullptr || amount_cap < norm_len ||
            out_currency_buf == nullptr || currency_cap < static_cast<int32_t>(currency.size()))
        {
            return VN_ERR_BUFFER_TOO_SMALL;
        }

        std::memcpy(out_amount_buf, norm_buf, static_cast<size_t>(norm_len) * sizeof(wchar_t));
        std::memcpy(out_currency_buf, currency.data(), currency.size() * sizeof(wchar_t));
        *out_ok = 1;
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_spotlight_ai_extract_response_text(
    const wchar_t* json_ptr, int32_t json_len, int32_t provider_kind,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len, uint8_t* out_ok) noexcept
{
    try
    {
        if (out_len == nullptr || out_ok == nullptr || json_len < 0)
            return VN_ERR_INVALID_ARG;
        *out_len = 0;
        *out_ok = 0;

        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        std::u16string result;
        auto iter_array = [](JsonSpan arr, auto&& fn) -> bool
        {
            if (arr.kind != JsonKind::Array || arr.raw.size() < 2) return false;
            size_t pos = 1;
            skip_json_ws(arr.raw, pos);
            if (pos < arr.raw.size() && arr.raw[pos] == u']') return true;
            size_t idx = 0;
            while (pos < arr.raw.size())
            {
                JsonSpan elem{};
                if (!json_parse_value(arr.raw, pos, elem, 1)) return false;
                if (!fn(idx++, elem)) return false;
                skip_json_ws(arr.raw, pos);
                if (pos >= arr.raw.size()) return false;
                if (arr.raw[pos] == u']') return true;
                if (arr.raw[pos] != u',') return false;
                ++pos;
            }
            return false;
        };

        if (provider_kind == 1) // Gemini
        {
            JsonSpan candidates{};
            if (!json_try_get_property(root, u"candidates", candidates) || candidates.kind != JsonKind::Array)
                return VN_OK;

            JsonSpan first_cand{};
            bool has_first = false;
            if (!iter_array(candidates, [&](size_t idx, JsonSpan elem) -> bool
            {
                if (idx == 0)
                {
                    first_cand = elem;
                    has_first = true;
                }
                return true;
            }) || !has_first || first_cand.kind != JsonKind::Object)
            {
                return VN_OK;
            }

            JsonSpan content{}, parts{};
            if (!json_try_get_property(first_cand, u"content", content) || content.kind != JsonKind::Object ||
                !json_try_get_property(content, u"parts", parts) || parts.kind != JsonKind::Array)
            {
                return VN_OK;
            }

            std::u16string piece;
            if (!iter_array(parts, [&](size_t, JsonSpan p) -> bool
            {
                if (p.kind != JsonKind::Object) return false;
                JsonSpan text_prop{};
                if (json_try_get_property(p, u"text", text_prop))
                {
                    JsonSpan thought{};
                    if (!json_try_get_property(p, u"thought", thought) || thought.kind != JsonKind::True)
                    {
                        if (text_prop.kind == JsonKind::String)
                        {
                            if (!json_decode_string(text_prop, piece)) return false;
                            result.append(piece);
                        }
                        else if (text_prop.kind != JsonKind::Null)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }))
            {
                return VN_OK;
            }
        }
        else if (provider_kind == 2) // Claude
        {
            JsonSpan content{};
            if (!json_try_get_property(root, u"content", content) || content.kind != JsonKind::Array)
                return VN_OK;

            std::u16string type_str, piece;
            if (!iter_array(content, [&](size_t, JsonSpan p) -> bool
            {
                if (p.kind != JsonKind::Object) return false;
                JsonSpan type_prop{};
                if (!json_try_get_property(p, u"type", type_prop)) return false;
                if (type_prop.kind == JsonKind::Null) return true;
                if (type_prop.kind != JsonKind::String || !json_decode_string(type_prop, type_str))
                    return false;
                if (type_str == u"text")
                {
                    JsonSpan text_prop{};
                    if (!json_try_get_property(p, u"text", text_prop)) return false;
                    if (text_prop.kind == JsonKind::String)
                    {
                        if (!json_decode_string(text_prop, piece)) return false;
                        result.append(piece);
                    }
                    else if (text_prop.kind != JsonKind::Null)
                    {
                        return false;
                    }
                }
                return true;
            }))
            {
                return VN_OK;
            }
        }
        else // OpenAI / DeepSeek / Default
        {
            JsonSpan choices{};
            if (!json_try_get_property(root, u"choices", choices) || choices.kind != JsonKind::Array)
                return VN_OK;

            JsonSpan first_choice{};
            bool has_first = false;
            if (!iter_array(choices, [&](size_t idx, JsonSpan elem) -> bool
            {
                if (idx == 0)
                {
                    first_choice = elem;
                    has_first = true;
                }
                return true;
            }) || !has_first || first_choice.kind != JsonKind::Object)
            {
                return VN_OK;
            }

            JsonSpan message{}, content{};
            if (!json_try_get_property(first_choice, u"message", message) || message.kind != JsonKind::Object ||
                !json_try_get_property(message, u"content", content))
            {
                return VN_OK;
            }
            if (content.kind == JsonKind::Null) return VN_OK;
            if (content.kind != JsonKind::String || !json_decode_string(content, result))
                return VN_OK;
        }

        if (vnotch::trim_view(result).empty()) return VN_OK;
        *out_ok = 1;
        return write_wstr_out(result, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_classify_text_traits(
    const wchar_t* text_ptr, int32_t text_len,
    uint8_t* out_has_email, uint8_t* out_looks_like_code) noexcept
{
    if ((out_has_email == nullptr && out_looks_like_code == nullptr) ||
        text_len < 0 || (text_len > 0 && text_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    const std::u16string_view text = make_u16_view(text_ptr, text_len);
    if (out_has_email != nullptr)
        *out_has_email = contains_clipboard_email(text) ? 1 : 0;
    if (out_looks_like_code != nullptr)
        *out_looks_like_code = looks_like_clipboard_code(text) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_is_public_ip_bytes(
    const uint8_t* addr_ptr, int32_t addr_len, uint8_t* out_is_public) noexcept
{
    if (out_is_public == nullptr || addr_ptr == nullptr || (addr_len != 4 && addr_len != 16))
        return VN_ERR_INVALID_ARG;
    *out_is_public = 0;

    const uint8_t* b = addr_ptr;
    int32_t len = addr_len;

    // Unwrap IPv4-mapped IPv6 (::ffff:a.b.c.d) if passed directly as 16 bytes.
    if (len == 16 &&
        b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
        b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
        b[8] == 0 && b[9] == 0 && b[10] == 0xFFu && b[11] == 0xFFu)
    {
        b += 12;
        len = 4;
    }

    bool pub = false;
    if (len == 4)
    {
        pub = b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
              !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
              !(b[0] == 169 && b[1] == 254) &&
              !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
              !(b[0] == 192 && (b[1] == 168 || b[1] == 0)) &&
              !(b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }
    else
    {
        pub = (b[0] & 0xE0u) == 0x20u &&
              !(b[0] == 0x20u && b[1] == 2u) &&
              !(b[0] == 0x20u && b[1] == 1u && b[2] == 0u && b[3] == 0u);
    }

    *out_is_public = pub ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_drop_is_blocked_device_path(
    const wchar_t* path_ptr, int32_t path_len, uint8_t* out_blocked) noexcept
{
    if (out_blocked == nullptr || path_len < 0 || (path_len > 0 && path_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_blocked = 0;

    const std::u16string_view raw = vnotch::trim_view(make_u16_view(path_ptr, path_len));
    if (raw.empty()) return VN_OK;

    auto is_sep = [](char16_t c) noexcept { return c == u'\\' || c == u'/'; };

    // Win32 / NT device namespace prefixes: \\.\, \\?\, \??\ (with \ or /)
    if (raw.size() >= 4 && is_sep(raw[0]))
    {
        if (is_sep(raw[1]) && (raw[2] == u'.' || raw[2] == u'?') && is_sep(raw[3]))
        {
            *out_blocked = 1;
            return VN_OK;
        }
        if (raw[1] == u'?' && raw[2] == u'?' && is_sep(raw[3]))
        {
            *out_blocked = 1;
            return VN_OK;
        }
    }

    // Check every path component after drive root for reserved DOS device names.
    size_t start = 0;
    if (raw.size() >= 2 && is_ascii_alpha(raw[0]) && raw[1] == u':')
        start = 2;

    while (start < raw.size())
    {
        while (start < raw.size() && is_sep(raw[start])) ++start;
        if (start >= raw.size()) break;
        size_t end = start;
        while (end < raw.size() && !is_sep(raw[end])) ++end;

        std::u16string_view seg = raw.substr(start, end - start);
        // Strip alternate data stream suffix and trailing dots/spaces normalized by Win32.
        const size_t colon = seg.find(u':');
        if (colon != std::u16string_view::npos) seg = seg.substr(0, colon);
        while (!seg.empty() && (seg.back() == u' ' || seg.back() == u'.'))
            seg.remove_suffix(1);
        const size_t dot = seg.find(u'.');
        if (dot != std::u16string_view::npos) seg = seg.substr(0, dot);
        while (!seg.empty() && seg.back() == u' ')
            seg.remove_suffix(1);

        if (equals_ascii_icase(seg, u"CON") ||
            equals_ascii_icase(seg, u"PRN") ||
            equals_ascii_icase(seg, u"AUX") ||
            equals_ascii_icase(seg, u"NUL") ||
            equals_ascii_icase(seg, u"CONIN$") ||
            equals_ascii_icase(seg, u"CONOUT$"))
        {
            *out_blocked = 1;
            return VN_OK;
        }

        if (seg.size() == 4 &&
            (equals_ascii_icase(seg.substr(0, 3), u"COM") ||
             equals_ascii_icase(seg.substr(0, 3), u"LPT")))
        {
            const char16_t d = seg[3];
            if ((d >= u'1' && d <= u'9') || d == 0x00B9u || d == 0x00B2u || d == 0x00B3u)
            {
                *out_blocked = 1;
                return VN_OK;
            }
        }

        start = end;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_drop_extract_html_img_src(
    const wchar_t* html_ptr, int32_t html_len,
    int32_t* out_offset, int32_t* out_length, uint8_t* out_found) noexcept
{
    if (out_offset == nullptr || out_length == nullptr || out_found == nullptr ||
        html_len < 0 || (html_len > 0 && html_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_offset = 0;
    *out_length = 0;
    *out_found = 0;

    const std::u16string_view html = make_u16_view(html_ptr, html_len);
    const size_t n = html.size();

    for (size_t i = 0; i + 4 < n; ++i)
    {
        if (html[i] != u'<' || !equals_ascii_icase(html.substr(i + 1, 3), u"img") ||
            !is_word_boundary(html, i + 4))
        {
            continue;
        }

        for (size_t k = i + 4; k < n && html[k] != u'>'; ++k)
        {
            if (k + 3 <= n && !is_regex_word_char(html[k - 1]) &&
                equals_ascii_icase(html.substr(k, 3), u"src") &&
                !is_regex_word_char(k + 3 < n ? html[k + 3] : u' '))
            {
                size_t p = k + 3;
                while (p < n && vnotch::is_unicode_whitespace(html[p])) ++p;
                if (p >= n || html[p] != u'=') continue;
                ++p;
                while (p < n && vnotch::is_unicode_whitespace(html[p])) ++p;
                if (p >= n) break;

                if (html[p] == u'"')
                {
                    const size_t val_start = p + 1;
                    size_t val_end = val_start;
                    while (val_end < n && html[val_end] != u'"') ++val_end;
                    if (val_end > val_start && val_end < n)
                    {
                        *out_offset = static_cast<int32_t>(val_start);
                        *out_length = static_cast<int32_t>(val_end - val_start);
                        *out_found = 1;
                        return VN_OK;
                    }
                }
                if (html[p] == u'\'')
                {
                    const size_t val_start = p + 1;
                    size_t val_end = val_start;
                    while (val_end < n && html[val_end] != u'\'') ++val_end;
                    if (val_end > val_start && val_end < n)
                    {
                        *out_offset = static_cast<int32_t>(val_start);
                        *out_length = static_cast<int32_t>(val_end - val_start);
                        *out_found = 1;
                        return VN_OK;
                    }
                }
                {
                    const size_t val_start = p;
                    size_t val_end = val_start;
                    while (val_end < n && !vnotch::is_unicode_whitespace(html[val_end]) && html[val_end] != u'>')
                        ++val_end;
                    if (val_end > val_start)
                    {
                        *out_offset = static_cast<int32_t>(val_start);
                        *out_length = static_cast<int32_t>(val_end - val_start);
                        *out_found = 1;
                        return VN_OK;
                    }
                }
            }
        }
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_drop_extract_source_url(
    const wchar_t* html_ptr, int32_t html_len,
    int32_t* out_offset, int32_t* out_length, uint8_t* out_found) noexcept
{
    if (out_offset == nullptr || out_length == nullptr || out_found == nullptr ||
        html_len < 0 || (html_len > 0 && html_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_offset = 0;
    *out_length = 0;
    *out_found = 0;

    const std::u16string_view html = make_u16_view(html_ptr, html_len);
    constexpr std::u16string_view kPrefix = u"SourceURL:";
    size_t line_start = 0;
    while (line_start < html.size())
    {
        if (line_start + kPrefix.size() < html.size() &&
            equals_ascii_icase(html.substr(line_start, kPrefix.size()), kPrefix))
        {
            const size_t val_start = line_start + kPrefix.size();
            size_t val_end = val_start;
            while (val_end < html.size() && html[val_end] != u'\r' && html[val_end] != u'\n')
                ++val_end;
            if (val_end > val_start)
            {
                *out_offset = static_cast<int32_t>(val_start);
                *out_length = static_cast<int32_t>(val_end - val_start);
                *out_found = 1;
                return VN_OK;
            }
        }
        const size_t nl = html.find(u'\n', line_start);
        if (nl == std::u16string_view::npos) break;
        line_start = nl + 1;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_is_secret_filename(
    const wchar_t* path_ptr, int32_t path_len, uint8_t* out_is_secret) noexcept
{
    if (out_is_secret == nullptr || path_len < 0 || (path_len > 0 && path_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_is_secret = 0;

    const std::u16string_view raw = make_u16_view(path_ptr, path_len);
    if (vnotch::trim_view(raw).empty()) return VN_OK;

    // Match .NET Path.GetFileName on Windows: drive prefix "C:" is root only at index 0..2,
    // while '\' and '/' split directory components anywhere.
    size_t root = (raw.size() >= 2 && is_ascii_alpha(raw[0]) && raw[1] == u':') ? 2u : 0u;
    size_t name_start = raw.size();
    while (name_start > root)
    {
        const char16_t c = raw[name_start - 1];
        if (c == u'\\' || c == u'/') break;
        --name_start;
    }
    const std::u16string_view file_name = raw.substr(name_start);
    if (file_name.empty()) return VN_OK;

    static constexpr std::u16string_view kSecretNames[] = {
        u"id_rsa", u"id_ed25519", u"id_ecdsa", u"id_dsa",
        u".npmrc", u".netrc", u"credentials", u".env"
    };
    for (std::u16string_view secret : kSecretNames)
    {
        if (equals_ascii_icase(file_name, secret))
        {
            *out_is_secret = 1;
            return VN_OK;
        }
    }

    const std::u16string_view ext = vnotch::get_extension(file_name);
    if (!ext.empty())
    {
        static constexpr std::u16string_view kSecretExts[] = {
            u".pem", u".key", u".pfx", u".p12", u".kdbx", u".env", u".ppk", u".ovpn"
        };
        for (std::u16string_view secret_ext : kSecretExts)
        {
            if (equals_ascii_icase(ext, secret_ext))
            {
                *out_is_secret = 1;
                return VN_OK;
            }
        }
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_blob_build_nonce_aad(
    const uint8_t* stream_id_ptr, int32_t stream_id_len,
    uint64_t chunk_index, int32_t plain_len, uint8_t is_final,
    uint8_t* out_nonce, int32_t nonce_cap,
    uint8_t* out_aad, int32_t aad_cap) noexcept
{
    if (stream_id_ptr == nullptr || stream_id_len < 12 ||
        out_nonce == nullptr || nonce_cap < 12 ||
        out_aad == nullptr || aad_cap < 33)
    {
        return VN_ERR_INVALID_ARG;
    }

    std::memcpy(out_nonce, stream_id_ptr, 12);
    uint64_t low = 0;
    std::memcpy(&low, out_nonce + 4, sizeof(uint64_t));
    low ^= chunk_index;
    std::memcpy(out_nonce + 4, &low, sizeof(uint64_t));

    static constexpr uint8_t kMagic[8] = {'V', 'N', 'P', 'B', 'L', 'O', 'B', '1'};
    std::memcpy(out_aad, kMagic, 8);
    std::memcpy(out_aad + 8, stream_id_ptr, 12);
    std::memcpy(out_aad + 20, &chunk_index, sizeof(uint64_t));
    std::memcpy(out_aad + 28, &plain_len, sizeof(int32_t));
    out_aad[32] = is_final != 0 ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_blob_compute_geometry(
    int64_t file_len,
    int64_t* out_total_chunks,
    int32_t* out_last_plain_len,
    int64_t* out_plain_length,
    int32_t* out_error_kind) noexcept
{
    if (out_total_chunks == nullptr || out_last_plain_len == nullptr ||
        out_plain_length == nullptr || out_error_kind == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kHeaderSize = 20;
    constexpr int64_t kChunkSize = 64 * 1024;
    constexpr int64_t kTagSize = 16;
    constexpr int64_t kDiskChunkSize = kChunkSize + kTagSize;

    *out_total_chunks = 0;
    *out_last_plain_len = 0;
    *out_plain_length = 0;
    *out_error_kind = 0;

    if (file_len < kHeaderSize + kTagSize)
    {
        *out_error_kind = 1; // Truncated
        return VN_OK;
    }

    const int64_t body_len = file_len - kHeaderSize;
    if (body_len == kTagSize)
    {
        *out_total_chunks = 1;
        *out_last_plain_len = 0;
        *out_plain_length = 0;
        return VN_OK;
    }

    const int64_t rem = body_len % kDiskChunkSize;
    if (rem > 0 && rem <= kTagSize)
    {
        *out_error_kind = 2; // Corrupt length
        return VN_OK;
    }

    if (rem == 0)
    {
        *out_total_chunks = body_len / kDiskChunkSize;
        *out_last_plain_len = static_cast<int32_t>(kChunkSize);
    }
    else
    {
        *out_total_chunks = body_len / kDiskChunkSize + 1;
        *out_last_plain_len = static_cast<int32_t>(rem - kTagSize);
    }

    *out_plain_length = (*out_total_chunks - 1) * kChunkSize + *out_last_plain_len;
    return VN_OK;
}
