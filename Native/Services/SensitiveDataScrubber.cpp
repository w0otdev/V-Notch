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

static constexpr std::u16string_view SCRUB_SESSION_EXACT_FIELDS[] = {
    u"sp_dc", u"connect.sid", u"sid",
    u"JSESSIONID", u"PHPSESSID", u"ASP.NET_SessionId", u".AspNetCore.Cookies"
};

static constexpr std::u16string_view SCRUB_SESSION_SUFFIX_FIELDS[] = {
    u"token", u"access_token", u"refresh_token", u"session_token", u"id_token", u"auth_token",
    u"accesstoken", u"refreshtoken", u"sessiontoken", u"idtoken", u"authtoken",
    u"secret", u"client_secret", u"jwt_secret", u"app_secret", u"api_secret",
    u"clientsecret", u"jwtsecret", u"appsecret", u"apisecret",
    u"session", u"sessionid", u"session_id", u"session-id"
};

static constexpr std::u16string_view SCRUB_KEY_SUFFIXES[] = {
    u"apikey", u"api_key", u"api-key",
    u"privatekey", u"private_key", u"private-key",
    u"secretkey", u"secret_key", u"secret-key",
    u"accesskey", u"access_key", u"access-key"
};

inline bool is_ascii_alnum(char16_t c) noexcept
{
    return (c >= u'0' && c <= u'9') || (c >= u'A' && c <= u'Z') || (c >= u'a' && c <= u'z');
}

inline bool is_scrub_trigger(char16_t c) noexcept
{
    return c == u':' || c == u'=' || c == u'B' || c == u'b' || c == u'-' || c == u'.' ||
           c == u'A' || c == u'g' || c == u's' || c == u'r' || c == u'x';
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

inline bool try_decode_scrub_field_name(std::u16string_view key, char16_t tail[129], int32_t& out_length) noexcept
{
    char16_t ring[128]{};
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
        ring[out_length++ & 127] = c;
    }
    int32_t count = std::min<int32_t>(out_length, 128);
    for (int32_t i = 0; i < count; ++i)
    {
        tail[i] = ring[(out_length - count + i) & 127];
    }
    return true;
}

inline bool has_scrub_field_suffix(std::u16string_view key, std::u16string_view suffix, bool complete_name = true) noexcept
{
    if (key.size() < suffix.size() || !equals_ignore_case(key.substr(key.size() - suffix.size()), suffix))
    {
        return false;
    }
    if (key.size() == suffix.size())
    {
        return complete_name;
    }
    size_t prefix_len = key.size() - suffix.size();
    char16_t sep = key[prefix_len - 1];
    if (sep == u'_' || sep == u'-' || sep == u'.' || sep == u'/' || sep == u':')
    {
        return true;
    }
    char16_t suffix_first = key[prefix_len];
    return (suffix_first >= u'A' && suffix_first <= u'Z');
}

inline bool is_scrub_password_field(std::u16string_view key, bool complete_name = true) noexcept
{
    if (key.empty())
    {
        return false;
    }
    auto ends_ic = [&](std::u16string_view suffix) noexcept {
        return key.size() >= suffix.size() && equals_ignore_case(key.substr(key.size() - suffix.size()), suffix);
    };
    return ends_ic(u"password") ||
           ends_ic(u"passwd") ||
           ends_ic(u"passphrase") ||
           has_scrub_field_suffix(key, u"pwd", complete_name);
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
    for (std::u16string_view suffix : SCRUB_KEY_SUFFIXES)
    {
        if (key.size() >= suffix.size() && equals_ignore_case(key.substr(key.size() - suffix.size()), suffix))
        {
            return true;
        }
    }
    return false;
}

inline bool is_scrub_session_field(std::u16string_view key, bool complete_name = true) noexcept
{
    if (key.empty())
    {
        return false;
    }
    if (complete_name)
    {
        for (std::u16string_view name : SCRUB_SESSION_EXACT_FIELDS)
        {
            if (equals_ignore_case(key, name))
            {
                return true;
            }
        }
    }
    for (std::u16string_view suffix : SCRUB_SESSION_SUFFIX_FIELDS)
    {
        if (has_scrub_field_suffix(key, suffix, complete_name))
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
    if (end > cursor && (input[static_cast<size_t>(end - 1)] == u'"' || input[static_cast<size_t>(end - 1)] == u'\'') &&
        !is_scrub_escaped_quote(input, end - 1))
    {
        quote = input[static_cast<size_t>(--end)];
    }
    const int32_t min_start = (end - cursor > 128) ? (end - 128) : cursor;
    int32_t key_start = end;
    if (quote != 0)
    {
        bool found_open_quote = false;
        while (key_start > min_start)
        {
            --key_start;
            if (input[static_cast<size_t>(key_start)] == quote && !is_scrub_escaped_quote(input, key_start))
            {
                found_open_quote = true;
                break;
            }
        }
        if (!found_open_quote)
        {
            return false;
        }
        ++key_start;
    }
    else
    {
        while (key_start > min_start && is_scrub_field_char(input[static_cast<size_t>(key_start - 1)]))
        {
            --key_start;
        }
    }

    std::u16string_view key = input.substr(static_cast<size_t>(key_start), static_cast<size_t>(end - key_start));
    char16_t decoded[129]{};
    bool complete_name = !(quote == 0 && key_start > cursor && is_scrub_field_char(input[static_cast<size_t>(key_start - 1)]));
    if (quote == u'"' && key.find(u'\\') != std::u16string_view::npos)
    {
        int32_t dec_len = 0;
        if (!try_decode_scrub_field_name(key, decoded, dec_len))
        {
            return false;
        }
        complete_name = (dec_len <= 128);
        const int32_t clamped = std::clamp<int32_t>(dec_len, 0, 128);
        key = std::u16string_view(decoded, static_cast<size_t>(clamped));
    }

    bool password = is_scrub_password_field(key, complete_name);
    bool query = (key_start > 0 && (input[static_cast<size_t>(key_start - 1)] == u'?' || input[static_cast<size_t>(key_start - 1)] == u'&'));
    if (!password && !is_scrub_api_key_field(key) && !is_scrub_session_field(key, complete_name) &&
        !(query && complete_name && equals_ignore_case(key, u"key")))
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
    const int32_t min_begin = (end > 32) ? (end - 32) : 0;
    while (begin > min_begin && is_scrub_field_char(input[static_cast<size_t>(begin - 1)]))
    {
        --begin;
    }
    if (begin > 0 && is_scrub_field_char(input[static_cast<size_t>(begin - 1)]))
    {
        return false;
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

inline bool is_aws_key_char(char16_t c) noexcept
{
    return (c >= u'A' && c <= u'Z') || (c >= u'0' && c <= u'9');
}

inline bool has_aws_access_key_body(std::u16string_view text) noexcept
{
    if (text.size() < 20)
    {
        return false;
    }
    for (size_t i = 4; i < 20; ++i)
    {
        if (!is_aws_key_char(text[i]))
        {
            return false;
        }
    }
    return text.size() == 20 || !(is_ascii_alnum(text[20]) || text[20] == u'_' || text[20] == u'-');
}

inline bool has_github_token_prefix(std::u16string_view text) noexcept
{
    if (starts_with_ordinal(text, u"ghp_") ||
        starts_with_ordinal(text, u"gho_") ||
        starts_with_ordinal(text, u"ghu_") ||
        starts_with_ordinal(text, u"ghs_") ||
        starts_with_ordinal(text, u"ghr_"))
    {
        return has_scrub_token_alphabet(text.substr(4), 16, false);
    }
    if (starts_with_ordinal(text, u"github_pat_"))
    {
        return has_scrub_token_alphabet(text.substr(11), 16, false);
    }
    return false;
}

inline bool has_stripe_token_prefix(std::u16string_view text) noexcept
{
    if (starts_with_ordinal(text, u"sk_live_") ||
        starts_with_ordinal(text, u"rk_live_") ||
        starts_with_ordinal(text, u"sk_test_") ||
        starts_with_ordinal(text, u"rk_test_"))
    {
        return has_scrub_token_alphabet(text.substr(8), 10, false);
    }
    return false;
}

inline bool has_slack_token_prefix(std::u16string_view text) noexcept
{
    if (starts_with_ordinal(text, u"xoxb-") ||
        starts_with_ordinal(text, u"xoxp-") ||
        starts_with_ordinal(text, u"xoxa-") ||
        starts_with_ordinal(text, u"xoxr-") ||
        starts_with_ordinal(text, u"xoxs-"))
    {
        return has_scrub_token_alphabet(text.substr(5), 10, false);
    }
    return false;
}

inline bool try_read_scrub_pem_private_key(std::u16string_view input, int32_t index, ScrubberSecretStart& start) noexcept
{
    std::u16string_view text = input.substr(static_cast<size_t>(index));
    if (!starts_with_ordinal(text, u"-----BEGIN "))
    {
        return false;
    }
    std::u16string_view after_begin = text.substr(11);
    size_t max_header = std::min<size_t>(after_begin.size(), 64);
    std::u16string_view header_window = after_begin.substr(0, max_header);
    size_t close_dashes = header_window.find(u"-----");
    if (close_dashes == std::u16string_view::npos)
    {
        return false;
    }
    std::u16string_view label = header_window.substr(0, close_dashes);
    for (char16_t c : label)
    {
        if (c == u'\r' || c == u'\n')
        {
            return false;
        }
    }
    if (label.find(u"PRIVATE KEY") == std::u16string_view::npos)
    {
        return false;
    }

    size_t body_offset = 11 + close_dashes + 5;
    int32_t end = index + static_cast<int32_t>(body_offset);
    int32_t len = static_cast<int32_t>(input.size());
    while (end < len && input[static_cast<size_t>(end)] != u'"' && input[static_cast<size_t>(end)] != u'\'')
    {
        if (input[static_cast<size_t>(end)] == u'-' &&
            starts_with_ordinal(input.substr(static_cast<size_t>(end)), u"-----END "))
        {
            std::u16string_view after_end = input.substr(static_cast<size_t>(end + 9));
            size_t end_window_len = std::min<size_t>(after_end.size(), 64);
            size_t end_close = after_end.substr(0, end_window_len).find(u"-----");
            if (end_close != std::u16string_view::npos)
            {
                start = { index, ScrubberSecretKind::Token, end + 9 + static_cast<int32_t>(end_close + 5) };
                return true;
            }
        }
        ++end;
    }
    start = { index, ScrubberSecretKind::Token, end };
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
    if (ch == u'-')
    {
        if (try_read_scrub_pem_private_key(input, index, start))
        {
            return true;
        }
        if (index - 2 >= cursor && equals_ordinal(input.substr(static_cast<size_t>(index - 2), 2), u"sk") &&
            is_scrub_word_start(input, index - 2) && has_scrub_token_alphabet(text.substr(1), 8, false))
        {
            value = index - 2;
        }
        else
        {
            return false;
        }
    }
    else if (ch == u'A')
    {
        if (is_scrub_word_start(input, index) &&
            (starts_with_ordinal(text, u"AKIA") || starts_with_ordinal(text, u"ASIA")) &&
            has_aws_access_key_body(text))
        {
            start = { index, ScrubberSecretKind::Token, index + 20 };
            return true;
        }
        if (is_scrub_word_start(input, index) &&
            starts_with_ordinal(text, u"AIza") &&
            has_scrub_token_alphabet(text.substr(4), 16, false))
        {
            value = index;
        }
        else
        {
            return false;
        }
    }
    else if (ch == u'g' && is_scrub_word_start(input, index) && has_github_token_prefix(text))
    {
        value = index;
    }
    else if ((ch == u's' || ch == u'r') && is_scrub_word_start(input, index) && has_stripe_token_prefix(text))
    {
        value = index;
    }
    else if (ch == u's' && is_scrub_word_start(input, index) &&
             starts_with_ordinal(text, u"sk-") && has_scrub_token_alphabet(text.substr(3), 8, false))
    {
        value = index;
    }
    else if (ch == u'x' && is_scrub_word_start(input, index) && has_slack_token_prefix(text))
    {
        value = index;
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
        *out_len = 0;
        *out_changed = 0u;
        return VN_ERR_FAIL;
    }
}
