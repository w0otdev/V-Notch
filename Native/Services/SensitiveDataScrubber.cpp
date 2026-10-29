#include "vnotch_common.h"

using namespace vnotch;

namespace
{
enum class ScrubberSecretKind : int32_t
{
    Token = 0,
    Password = 1,
    JsonValue = 2
};

struct ScrubberSecretStart
{
    int32_t value_index;
    ScrubberSecretKind kind;
    int32_t value_end;
};

static constexpr std::u16string_view SCRUB_REDACTED = u"[REDACTED]";

static constexpr std::u16string_view SCRUB_SESSION_FIELDS[] = {
    u"sp_dc", u"session", u"sessionid", u"session_id", u"session_token", u"connect.sid", u"sid",
    u"token", u"access_token", u"refresh_token", u"auth_token", u"secret", u"client_secret",
    u"JSESSIONID", u"PHPSESSID", u"ASP.NET_SessionId", u".AspNetCore.Cookies"
};

static constexpr std::u16string_view SCRUB_PASSWORD_FIELDS[] = {
    u"password", u"passwd", u"pwd"
};

inline bool is_ascii_alnum(char16_t c) noexcept
{
    return (c >= u'0' && c <= u'9') || (c >= u'A' && c <= u'Z') || (c >= u'a' && c <= u'z');
}

inline bool is_scrub_trigger(char16_t c) noexcept
{
    return c == u':' || c == u'=' || c == u'B' || c == u'b' || c == u'-' || c == u'.' || c == u'A';
}

inline bool is_scrub_token_boundary(char16_t c) noexcept
{
    return is_unicode_whitespace(c) || c == u'&' || c == u',' || c == u';' ||
           c == u'"' || c == u'\'' || c == u'}' || c == u']' || c == u')' || c == u'>';
}

inline bool is_scrub_word_start(std::u16string_view input, int32_t index) noexcept
{
    if (index <= 0)
    {
        return true;
    }
    char16_t prev = input[static_cast<size_t>(index - 1)];
    return !(vn_is_letter_or_digit(prev) || prev == u'_');
}

inline bool is_scrub_horizontal_space(char16_t c) noexcept
{
    return is_unicode_whitespace(c) && c != u'\r' && c != u'\n';
}

inline int32_t scrub_skip_space(std::u16string_view input, int32_t index) noexcept
{
    int32_t len = static_cast<int32_t>(input.size());
    while (index < len && is_scrub_horizontal_space(input[static_cast<size_t>(index)]))
    {
        ++index;
    }
    return index;
}

inline bool is_scrub_field_char(char16_t c) noexcept
{
    return vn_is_letter_or_digit(c) || c == u'_' || c == u'-' || c == u'.';
}

inline bool is_scrub_escaped_quote(std::u16string_view input, int32_t index) noexcept
{
    int32_t backslashes = 0;
    while (index > 0 && input[static_cast<size_t>(--index)] == u'\\')
    {
        ++backslashes;
    }
    return (backslashes & 1) != 0;
}

inline bool try_decode_scrub_field_name(std::u16string_view key, char16_t tail[33], int32_t& out_length) noexcept
{
    char16_t ring[32]{};
    out_length = 0;
    for (size_t i = 0; i < key.size(); ++i)
    {
        char16_t c = key[i];
        if (c == u'\\')
        {
            if (++i >= key.size())
            {
                return false;
            }
            c = key[i];
            if (c == u'u')
            {
                if (i + 4 >= key.size())
                {
                    return false;
                }
                int32_t val = 0;
                for (int n = 0; n < 4; ++n)
                {
                    char16_t hex = key[++i];
                    int32_t digit = (hex >= u'0' && hex <= u'9') ? (hex - u'0')
                                  : (hex >= u'a' && hex <= u'f') ? (hex - u'a' + 10)
                                  : (hex >= u'A' && hex <= u'F') ? (hex - u'A' + 10) : -1;
                    if (digit < 0)
                    {
                        return false;
                    }
                    val = (val << 4) | digit;
                }
                c = static_cast<char16_t>(val);
            }
            else
            {
                switch (c)
                {
                    case u'"': c = u'"'; break;
                    case u'\\': c = u'\\'; break;
                    case u'/': c = u'/'; break;
                    case u'b': c = u'\b'; break;
                    case u'f': c = u'\f'; break;
                    case u'n': c = u'\n'; break;
                    case u'r': c = u'\r'; break;
                    case u't': c = u'\t'; break;
                    default: return false;
                }
            }
        }
        ring[out_length++ & 31] = c;
    }
    int32_t count = std::min<int32_t>(out_length, 32);
    for (int32_t i = 0; i < count; ++i)
    {
        tail[i] = ring[(out_length - count + i) & 31];
    }
    return true;
}

inline bool is_scrub_password_field(std::u16string_view key) noexcept
{
    if (key.empty())
    {
        return false;
    }
    char16_t last = key.back();
    if (last != u'd' && last != u'D')
    {
        return false;
    }
    for (std::u16string_view name : SCRUB_PASSWORD_FIELDS)
    {
        if (key.size() >= name.size() && equals_ignore_case(key.substr(key.size() - name.size()), name))
        {
            size_t prefix = key.size() - name.size();
            if (prefix == 0 || !(vn_is_letter_or_digit(key[prefix - 1]) || key[prefix - 1] == u'_'))
            {
                return true;
            }
        }
    }
    return false;
}

inline bool is_scrub_api_key_field(std::u16string_view key) noexcept
{
    if (key.empty())
    {
        return false;
    }
    char16_t last = key.back();
    if (last != u'y' && last != u'Y')
    {
        return false;
    }
    auto ends_ic = [&](std::u16string_view suffix) noexcept {
        return key.size() >= suffix.size() && equals_ignore_case(key.substr(key.size() - suffix.size()), suffix);
    };
    return ends_ic(u"apikey") || ends_ic(u"api_key") || ends_ic(u"api-key");
}

inline bool is_scrub_session_field(std::u16string_view key) noexcept
{
    if (key.empty())
    {
        return false;
    }
    for (std::u16string_view name : SCRUB_SESSION_FIELDS)
    {
        if (equals_ignore_case(key, name))
        {
            return true;
        }
    }
    return false;
}

inline bool try_read_scrub_field(std::u16string_view input, int32_t delimiter, int32_t cursor, ScrubberSecretStart& start) noexcept
{
    start = { 0, ScrubberSecretKind::Token, -1 };
    char16_t delim_ch = input[static_cast<size_t>(delimiter)];
    if (delim_ch != u':' && delim_ch != u'=')
    {
        return false;
    }
    int32_t end = delimiter;
    while (end > cursor && is_unicode_whitespace(input[static_cast<size_t>(end - 1)]))
    {
        --end;
    }
    char16_t quote = 0;
    if (end > cursor && (input[static_cast<size_t>(end - 1)] == u'"' || input[static_cast<size_t>(end - 1)] == u'\''))
    {
        quote = input[static_cast<size_t>(--end)];
    }
    int32_t key_start = end;
    if (quote != 0)
    {
        while (key_start > cursor)
        {
            --key_start;
            if (input[static_cast<size_t>(key_start)] == quote && !is_scrub_escaped_quote(input, key_start))
            {
                break;
            }
        }
        if (key_start == end || input[static_cast<size_t>(key_start)] != quote)
        {
            return false;
        }
        ++key_start;
    }
    else
    {
        while (key_start > cursor && is_scrub_field_char(input[static_cast<size_t>(key_start - 1)]))
        {
            --key_start;
        }
    }

    std::u16string_view key = input.substr(static_cast<size_t>(key_start), static_cast<size_t>(end - key_start));
    char16_t decoded[33]{};
    bool complete_name = true;
    if (quote == u'"' && key.find(u'\\') != std::u16string_view::npos)
    {
        int32_t dec_len = 0;
        if (!try_decode_scrub_field_name(key, decoded, dec_len))
        {
            return false;
        }
        complete_name = (dec_len <= 32);
        const int32_t clamped = std::clamp<int32_t>(dec_len, 0, 32);
        key = std::u16string_view(decoded, static_cast<size_t>(clamped));
    }

    bool password = is_scrub_password_field(key);
    bool query = (key_start > 0 && (input[static_cast<size_t>(key_start - 1)] == u'?' || input[static_cast<size_t>(key_start - 1)] == u'&'));
    if (!password && !is_scrub_api_key_field(key) && !(complete_name && is_scrub_session_field(key)) &&
        !(query && equals_ignore_case(key, u"key")))
    {
        return false;
    }

    int32_t value = scrub_skip_space(input, delimiter + 1);
    bool json = (quote == u'"' && delim_ch == u':' && key_start > 0 && input[static_cast<size_t>(key_start - 1)] == u'"');
    start = {
        value,
        json ? ScrubberSecretKind::JsonValue : (password ? ScrubberSecretKind::Password : ScrubberSecretKind::Token),
        -1
    };
    return true;
}

inline bool is_scrub_authorization_value(std::u16string_view input, int32_t index) noexcept
{
    int32_t end = index;
    while (end > 0 && is_scrub_horizontal_space(input[static_cast<size_t>(end - 1)]))
    {
        --end;
    }
    if (end > 0 && (input[static_cast<size_t>(end - 1)] == u'"' || input[static_cast<size_t>(end - 1)] == u'\''))
    {
        --end;
    }
    while (end > 0 && is_scrub_horizontal_space(input[static_cast<size_t>(end - 1)]))
    {
        --end;
    }
    if (end == 0)
    {
        return false;
    }
    char16_t d = input[static_cast<size_t>(--end)];
    if (d != u':' && d != u'=')
    {
        return false;
    }
    while (end > 0 && is_scrub_horizontal_space(input[static_cast<size_t>(end - 1)]))
    {
        --end;
    }
    if (end > 0 && (input[static_cast<size_t>(end - 1)] == u'"' || input[static_cast<size_t>(end - 1)] == u'\''))
    {
        --end;
    }
    int32_t begin = end;
    while (begin > 0 && is_scrub_field_char(input[static_cast<size_t>(begin - 1)]))
    {
        --begin;
    }
    std::u16string_view key = input.substr(static_cast<size_t>(begin), static_cast<size_t>(end - begin));
    return equals_ignore_case(key, u"Authorization") || equals_ignore_case(key, u"Proxy-Authorization");
}

inline bool scrub_basic_has_credential_separator(std::u16string_view text) noexcept
{
    if (!text.empty() && (text[0] == u'"' || text[0] == u'\''))
    {
        text.remove_prefix(1);
    }
    uint32_t buffer = 0;
    int bits = 0;
    for (char16_t c : text)
    {
        int val = (c >= u'A' && c <= u'Z') ? (c - u'A')
                : (c >= u'a' && c <= u'z') ? (c - u'a' + 26)
                : (c >= u'0' && c <= u'9') ? (c - u'0' + 52)
                : (c == u'+') ? 62 : (c == u'/') ? 63 : -1;
        if (val < 0)
        {
            return false;
        }
        buffer = (buffer << 6) | static_cast<uint32_t>(val);
        bits += 6;
        if (bits < 8)
        {
            continue;
        }
        bits -= 8;
        if (static_cast<uint8_t>(buffer >> bits) == ':')
        {
            return true;
        }
    }
    return false;
}

inline bool try_read_scrub_authentication(std::u16string_view input, int32_t index, ScrubberSecretStart& start) noexcept
{
    start = { 0, ScrubberSecretKind::Token, -1 };
    char16_t ch = input[static_cast<size_t>(index)];
    if ((ch != u'B' && ch != u'b') || !is_scrub_word_start(input, index))
    {
        return false;
    }
    std::u16string_view text = input.substr(static_cast<size_t>(index));
    int32_t length = starts_with_ignore_case(text, u"Bearer") ? 6
                   : starts_with_ignore_case(text, u"Basic") ? 5 : 0;
    if (length == 0 || static_cast<int32_t>(text.size()) <= length || !is_scrub_horizontal_space(text[static_cast<size_t>(length)]))
    {
        return false;
    }
    int32_t value = scrub_skip_space(input, index + length);
    if (length == 5 && !is_scrub_authorization_value(input, index) &&
        !scrub_basic_has_credential_separator(input.substr(static_cast<size_t>(value))))
    {
        return false;
    }
    start = { value, ScrubberSecretKind::Token, -1 };
    return true;
}

inline bool has_scrub_token_alphabet(std::u16string_view text, int32_t minimum, bool encrypted) noexcept
{
    if (static_cast<int32_t>(text.size()) < minimum)
    {
        return false;
    }
    for (int32_t i = 0; i < minimum; ++i)
    {
        char16_t c = text[static_cast<size_t>(i)];
        bool ok = is_ascii_alnum(c) ||
                  (encrypted ? (c == u'+' || c == u'/' || c == u'=') : (c == u'_' || c == u'-'));
        if (!ok)
        {
            return false;
        }
    }
    return true;
}

inline bool is_scrub_base64_url(char16_t c) noexcept
{
    return is_ascii_alnum(c) || c == u'_' || c == u'-';
}

inline bool scrub_starts_with_json_object(std::u16string_view encoded) noexcept
{
    uint32_t buffer = 0;
    int bits = 0;
    for (char16_t c : encoded)
    {
        int val = (c >= u'A' && c <= u'Z') ? (c - u'A')
                : (c >= u'a' && c <= u'z') ? (c - u'a' + 26)
                : (c >= u'0' && c <= u'9') ? (c - u'0' + 52)
                : (c == u'-') ? 62 : 63;
        buffer = (buffer << 6) | static_cast<uint32_t>(val);
        bits += 6;
        if (bits < 8)
        {
            continue;
        }
        bits -= 8;
        uint8_t decoded = static_cast<uint8_t>(buffer >> bits);
        if (decoded != 9 && decoded != 10 && decoded != 13 && decoded != 32)
        {
            return decoded == '{';
        }
    }
    return false;
}

inline bool try_read_scrub_jwt(std::u16string_view input, int32_t separator, int32_t cursor, ScrubberSecretStart& start) noexcept
{
    start = { 0, ScrubberSecretKind::Token, -1 };
    int32_t header = separator;
    while (header > cursor && is_scrub_base64_url(input[static_cast<size_t>(header - 1)]))
    {
        --header;
    }
    if (header == separator || !scrub_starts_with_json_object(input.substr(static_cast<size_t>(header), static_cast<size_t>(separator - header))))
    {
        return false;
    }
    int32_t end = separator + 1;
    int32_t separators = 1;
    int32_t len = static_cast<int32_t>(input.size());
    while (end < len && (is_scrub_base64_url(input[static_cast<size_t>(end)]) || input[static_cast<size_t>(end)] == u'.'))
    {
        if (input[static_cast<size_t>(end)] == u'.')
        {
            ++separators;
        }
        ++end;
    }
    if (separators < 2)
    {
        return false;
    }
    start = { header, ScrubberSecretKind::Token, end };
    return true;
}

inline bool try_read_scrub_standalone_token(std::u16string_view input, int32_t index, int32_t cursor, ScrubberSecretStart& start) noexcept
{
    start = { 0, ScrubberSecretKind::Token, -1 };
    std::u16string_view text = input.substr(static_cast<size_t>(index));
    int32_t value = 0;
    char16_t ch = input[static_cast<size_t>(index)];
    if (ch == u'-' && index - 2 >= cursor && equals_ordinal(input.substr(static_cast<size_t>(index - 2), 2), u"sk") &&
        is_scrub_word_start(input, index - 2) && has_scrub_token_alphabet(text.substr(1), 8, false))
    {
        value = index - 2;
    }
    else if (ch == u'A' && starts_with_ordinal(text, u"AIza") && has_scrub_token_alphabet(text.substr(4), 16, false))
    {
        value = index + 4;
    }
    else if (ch == u':' && index - 3 >= cursor && equals_ordinal(input.substr(static_cast<size_t>(index - 3), 3), u"enc") &&
             has_scrub_token_alphabet(text.substr(1), 16, true))
    {
        value = index + 1;
    }
    else if (ch == u'.')
    {
        return try_read_scrub_jwt(input, index, cursor, start);
    }
    else
    {
        return false;
    }

    int32_t end = value;
    int32_t len = static_cast<int32_t>(input.size());
    while (end < len && !is_scrub_token_boundary(input[static_cast<size_t>(end)]))
    {
        ++end;
    }
    start = { value, ScrubberSecretKind::Token, end };
    return true;
}

inline int32_t scrub_quoted_end(std::u16string_view input, int32_t start_idx, char16_t quote) noexcept
{
    int32_t end = start_idx + 1;
    int32_t len = static_cast<int32_t>(input.size());
    while (end < len)
    {
        if (input[static_cast<size_t>(end)] == u'\\' && end + 1 < len)
        {
            end += 2;
        }
        else if (input[static_cast<size_t>(end++)] == quote)
        {
            return end;
        }
    }
    return end;
}

inline int32_t scrub_append_redacted(std::u16string_view input, std::u16string& output, int32_t cursor, const ScrubberSecretStart& start)
{
    int32_t len = static_cast<int32_t>(input.size());
    int32_t value = start.value_index;
    if (start.kind == ScrubberSecretKind::JsonValue)
    {
        while (value < len && is_unicode_whitespace(input[static_cast<size_t>(value)]))
        {
            ++value;
        }
    }
    if (value < cursor) value = cursor;
    output.append(input.substr(static_cast<size_t>(cursor), static_cast<size_t>(value - cursor)));
    if (start.value_end >= value)
    {
        output.append(SCRUB_REDACTED);
        return start.value_end;
    }
    if (value < len && (input[static_cast<size_t>(value)] == u'"' || input[static_cast<size_t>(value)] == u'\''))
    {
        char16_t quote = input[static_cast<size_t>(value)];
        int32_t end = scrub_quoted_end(input, value, quote);
        output.push_back(quote);
        output.append(SCRUB_REDACTED);
        output.push_back(quote);
        return end;
    }
    int32_t stop = value;
    if (start.kind == ScrubberSecretKind::JsonValue)
    {
        int32_t depth = 0;
        while (stop < len)
        {
            char16_t c = input[static_cast<size_t>(stop)];
            if (c == u'"' || c == u'\'')
            {
                stop = scrub_quoted_end(input, stop, c);
                continue;
            }
            if (depth == 0 && (c == u',' || c == u'}' || c == u']' || c == u'\r' || c == u'\n'))
            {
                break;
            }
            if (c == u'{' || c == u'[')
            {
                ++depth;
            }
            else if (c == u'}' || c == u']')
            {
                --depth;
            }
            ++stop;
        }
        output.push_back(u'"');
        output.append(SCRUB_REDACTED);
        output.push_back(u'"');
    }
    else
    {
        if (starts_with_ordinal(input.substr(static_cast<size_t>(value)), SCRUB_REDACTED))
        {
            stop += static_cast<int32_t>(SCRUB_REDACTED.size());
        }
        while (stop < len && (start.kind == ScrubberSecretKind::Password
                              ? (input[static_cast<size_t>(stop)] != u'\r' && input[static_cast<size_t>(stop)] != u'\n')
                              : !is_scrub_token_boundary(input[static_cast<size_t>(stop)])))
        {
            ++stop;
        }
        output.append(SCRUB_REDACTED);
    }
    return stop;
}
} // namespace

VN_EXPORT int32_t vn_sensitive_data_scrub(
    const char16_t* input_ptr, int32_t input_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_changed) noexcept
{
    if (out_len == nullptr || out_changed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_len = 0;
    *out_changed = 0u;
    std::u16string_view input = make_u16_view(input_ptr, input_len);
    if (input.empty())
    {
        return VN_OK;
    }

    try
    {
        std::u16string output;
        bool changed = false;
        int32_t cursor = 0;
        int32_t index = 0;
        int32_t len = static_cast<int32_t>(input.size());

        while (index < len)
        {
            int32_t next_trigger = -1;
            for (int32_t i = index; i < len; ++i)
            {
                if (is_scrub_trigger(input[static_cast<size_t>(i)]))
                {
                    next_trigger = i;
                    break;
                }
            }
            if (next_trigger < 0)
            {
                break;
            }
            index = next_trigger;
            ScrubberSecretStart start{};
            if (try_read_scrub_field(input, index, cursor, start) ||
                try_read_scrub_authentication(input, index, start) ||
                try_read_scrub_standalone_token(input, index, cursor, start))
            {
                if (!changed)
                {
                    changed = true;
                    output.reserve(std::min<size_t>(input.size(), 1024));
                }
                cursor = scrub_append_redacted(input, output, cursor, start);
                index = cursor;
            }
            else
            {
                ++index;
            }
        }

        if (!changed)
        {
            return VN_OK;
        }

        output.append(input.substr(static_cast<size_t>(cursor)));
        *out_changed = 1u;
        return copy_u16_out(output, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

