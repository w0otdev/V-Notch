#pragma once

#include "vnotch_common.h"
#include "../vnotch_keyword_trie.h"

namespace vnotch::secret_patterns
{

// Shared token & credential prefixes
inline constexpr std::u16string_view kPrefixOpenAiSk = u"sk-";
inline constexpr std::u16string_view kPrefixOpenAiSkShort = u"sk";
inline constexpr std::u16string_view kSubprefixOpenAiProj = u"proj-";
inline constexpr std::u16string_view kSubprefixAnthropic = u"ant-";

inline constexpr std::u16string_view kGitHubClassicPrefixes[] = {
    u"ghp_", u"gho_", u"ghu_", u"ghs_", u"ghr_"
};
inline constexpr std::u16string_view kPrefixGitHubPat = u"github_pat_";

inline constexpr std::u16string_view kPrefixAwsAkia = u"AKIA";
inline constexpr std::u16string_view kPrefixAwsAsia = u"ASIA";
inline constexpr size_t kAwsAccessKeyPrefixLen = 4;
inline constexpr size_t kAwsAccessKeyBodyLen = 16;
inline constexpr size_t kAwsAccessKeyTotalLen = 20;

inline constexpr std::u16string_view kPrefixGoogleApiKey = u"AIza";

inline constexpr std::u16string_view kPrefixSlackRoot = u"xox";
inline constexpr std::u16string_view kSlackTokenPrefixes[] = {
    u"xoxb-", u"xoxp-", u"xoxa-", u"xoxr-", u"xoxs-"
};

inline constexpr std::u16string_view kStripeTokenPrefixes[] = {
    u"sk_live_", u"rk_live_", u"sk_test_", u"rk_test_"
};

inline constexpr std::u16string_view kPrefixSendGrid = u"SG.";
inline constexpr std::u16string_view kPrefixBearer = u"Bearer";
inline constexpr std::u16string_view kPrefixBasic = u"Basic";
inline constexpr std::u16string_view kPrefixEncrypted = u"enc";
inline constexpr std::u16string_view kPrefixWifi = u"WIFI:";
inline constexpr std::u16string_view kCookieSpotifyDc = u"sp_dc";

// Shared PEM private key header/footer markers
inline constexpr std::u16string_view kPemBeginPrefix = u"-----BEGIN ";
inline constexpr std::u16string_view kPemEndPrefix = u"-----END ";
inline constexpr std::u16string_view kPemDashes = u"-----";
inline constexpr std::u16string_view kPemPrivateKeyMarker = u"PRIVATE KEY";
inline constexpr std::u16string_view kPemPrivateKeyClose = u"PRIVATE KEY-----";
inline constexpr std::u16string_view kPemPrivateKeyBlockClose = u"PRIVATE KEY BLOCK-----";

// Shared sensitive field/query/label key-name tables & tries
struct SecretCompoundPair
{
    std::u16string_view first;
    std::u16string_view second;
};

inline constexpr std::u16string_view kSecretValueSuffixes[] = {
    u"key", u"token", u"value", u"id", u"code", u"string", u"hash"
};
inline constexpr auto kSecretValueSuffixTrie = vnotch::MakeKeywordTrie(kSecretValueSuffixes);

inline constexpr std::u16string_view kCredentialExactLabels[] = {
    u"passphrase", u"password", u"passcode", u"passwd", u"apikey",
    u"token", u"totp", u"pass", u"pwd", u"pin", u"otp", u"2fa"
};
inline constexpr auto kCredentialExactLabelTrie = vnotch::MakeKeywordTrie(kCredentialExactLabels);

inline constexpr SecretCompoundPair kCredentialCompoundLabels[] = {
    { u"private", u"key" }, { u"secret", u"key" }, { u"access", u"key" }, { u"api", u"key" },
    { u"client", u"secret" }, { u"auth", u"token" }, { u"access", u"token" },
    { u"refresh", u"token" }, { u"session", u"token" }, { u"id", u"token" },
    { u"verification", u"code" }
};

inline constexpr std::u16string_view kPasswordFieldSuffixes[] = {
    u"password", u"passwd", u"passphrase"
};
inline constexpr std::u16string_view kPasswordShortField = u"pwd";

inline constexpr std::u16string_view kScrubSessionExactFields[] = {
    u"token", u"secret", u"session",
    kCookieSpotifyDc, u"connect.sid", u"sid",
    u"JSESSIONID", u"PHPSESSID", u"ASP.NET_SessionId", u".AspNetCore.Cookies"
};
inline constexpr auto kScrubSessionExactTrie = vnotch::MakeKeywordTrie(kScrubSessionExactFields);

inline constexpr std::u16string_view kScrubSessionSuffixFields[] = {
    u"access_token", u"refresh_token", u"session_token", u"id_token", u"auth_token",
    u"accesstoken", u"refreshtoken", u"sessiontoken", u"idtoken", u"authtoken",
    u"client_secret", u"jwt_secret", u"app_secret", u"api_secret",
    u"clientsecret", u"jwtsecret", u"appsecret", u"apisecret",
    u"sessionid", u"session_id", u"session-id"
};
inline constexpr auto kScrubSessionSuffixTrie = vnotch::MakeKeywordTrie(kScrubSessionSuffixFields, true);

inline constexpr std::u16string_view kScrubApiKeySuffixes[] = {
    u"apikey", u"api_key", u"api-key",
    u"privatekey", u"private_key", u"private-key",
    u"secretkey", u"secret_key", u"secret-key",
    u"accesskey", u"access_key", u"access-key"
};
inline constexpr auto kScrubApiKeySuffixTrie = vnotch::MakeKeywordTrie(kScrubApiKeySuffixes, true);

// Shared character predicates
[[nodiscard]] constexpr bool is_ascii_digit(char16_t c) noexcept
{
    return c >= u'0' && c <= u'9';
}

[[nodiscard]] constexpr bool is_ascii_alpha(char16_t c) noexcept
{
    return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z');
}

[[nodiscard]] constexpr bool is_ascii_alnum(char16_t c) noexcept
{
    return is_ascii_alpha(c) || is_ascii_digit(c);
}

[[nodiscard]] constexpr bool is_ascii_alnum_under(char16_t c) noexcept
{
    return is_ascii_alnum(c) || c == u'_';
}

[[nodiscard]] constexpr bool is_alnum_dash_under(char16_t c) noexcept
{
    return is_ascii_alnum(c) || c == u'_' || c == u'-';
}

[[nodiscard]] constexpr bool is_bearer_token_char(char16_t c) noexcept
{
    return is_ascii_alnum(c) || c == u'.' || c == u'_' || c == u'~' || c == u'+' || c == u'/' || c == u'-';
}

[[nodiscard]] constexpr bool is_aws_upper_alnum(char16_t c) noexcept
{
    return (c >= u'A' && c <= u'Z') || is_ascii_digit(c);
}

[[nodiscard]] constexpr bool is_ascii_hex(char16_t c) noexcept
{
    return is_ascii_digit(c) || (c >= u'a' && c <= u'f') || (c >= u'A' && c <= u'F');
}

[[nodiscard]] constexpr int32_t ascii_hex_value(char16_t c) noexcept
{
    if (c >= u'0' && c <= u'9') return c - u'0';
    if (c >= u'a' && c <= u'f') return 10 + (c - u'a');
    if (c >= u'A' && c <= u'F') return 10 + (c - u'A');
    return -1;
}

// Shared token prefix & field-name matcher helpers
[[nodiscard]] inline bool starts_with_token_prefix(
    std::u16string_view text,
    std::u16string_view prefix,
    bool ignore_case) noexcept
{
    return ignore_case
        ? vnotch::starts_with_ignore_case(text, prefix)
        : vnotch::starts_with_ordinal(text, prefix);
}

[[nodiscard]] inline bool has_github_classic_prefix(std::u16string_view text, bool ignore_case) noexcept
{
    for (std::u16string_view prefix : kGitHubClassicPrefixes)
    {
        if (starts_with_token_prefix(text, prefix, ignore_case))
        {
            return true;
        }
    }
    return false;
}

[[nodiscard]] inline bool has_github_pat_prefix(std::u16string_view text, bool ignore_case) noexcept
{
    return starts_with_token_prefix(text, kPrefixGitHubPat, ignore_case);
}

[[nodiscard]] inline bool has_slack_token_prefix(std::u16string_view text, bool ignore_case) noexcept
{
    for (std::u16string_view prefix : kSlackTokenPrefixes)
    {
        if (starts_with_token_prefix(text, prefix, ignore_case))
        {
            return true;
        }
    }
    return false;
}

[[nodiscard]] inline bool has_stripe_token_prefix(std::u16string_view text, bool ignore_case) noexcept
{
    for (std::u16string_view prefix : kStripeTokenPrefixes)
    {
        if (starts_with_token_prefix(text, prefix, ignore_case))
        {
            return true;
        }
    }
    return false;
}

[[nodiscard]] inline bool has_aws_access_key_prefix(
    std::u16string_view text,
    bool allow_asia,
    bool ignore_case) noexcept
{
    if (starts_with_token_prefix(text, kPrefixAwsAkia, ignore_case))
    {
        return true;
    }
    return allow_asia && starts_with_token_prefix(text, kPrefixAwsAsia, ignore_case);
}

[[nodiscard]] inline bool has_field_name_suffix(
    std::u16string_view key,
    std::u16string_view suffix,
    bool complete_name = true) noexcept
{
    if (key.size() < suffix.size() ||
        !vnotch::equals_ignore_case(key.substr(key.size() - suffix.size()), suffix))
    {
        return false;
    }
    if (key.size() == suffix.size())
    {
        return complete_name;
    }
    const size_t prefix_len = key.size() - suffix.size();
    const char16_t sep = key[prefix_len - 1];
    if (sep == u'_' || sep == u'-' || sep == u'.' || sep == u'/' || sep == u':')
    {
        return true;
    }
    const char16_t suffix_first = key[prefix_len];
    return suffix_first >= u'A' && suffix_first <= u'Z';
}

[[nodiscard]] inline bool is_password_field_name(std::u16string_view key, bool complete_name = true) noexcept
{
    if (key.empty())
    {
        return false;
    }
    for (std::u16string_view suffix : kPasswordFieldSuffixes)
    {
        if (key.size() >= suffix.size() &&
            vnotch::equals_ignore_case(key.substr(key.size() - suffix.size()), suffix))
        {
            return true;
        }
    }
    return has_field_name_suffix(key, kPasswordShortField, complete_name);
}

[[nodiscard]] inline bool is_api_key_field_name(std::u16string_view key) noexcept
{
    if (key.empty())
    {
        return false;
    }
    const char16_t last = key.back();
    if (last != u'y' && last != u'Y')
    {
        return false;
    }
    return kScrubApiKeySuffixTrie.MatchesSuffix(key, [](size_t) noexcept { return true; });
}

[[nodiscard]] inline bool is_session_field_name(std::u16string_view key, bool complete_name = true) noexcept
{
    if (key.empty())
    {
        return false;
    }
    if (complete_name && kScrubSessionExactTrie.Contains(key))
    {
        return true;
    }
    return kScrubSessionSuffixTrie.MatchesSuffix(key, [&](size_t length) noexcept {
        return has_field_name_suffix(key, key.substr(key.size() - length), complete_name);
    });
}

} // namespace vnotch::secret_patterns
