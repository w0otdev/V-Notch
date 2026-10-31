#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ClipboardSensitiveData final
    {
    public:
        static constexpr std::u16string_view PM_NAMES[] = {
            u"1password", u"bitwarden", u"keepass", u"keepassxc", u"lastpass", u"enpass", u"dashlane",
            u"nordpass", u"protonpass", u"proton pass", u"roboform", u"authpass", u"keeper", u"safeincloud"
        };
        
        static constexpr std::u16string_view NON_VALUES[] = {
            u"null", u"undefined", u"true", u"false", u"none", u"required", u"optional", u"missing", u"invalid",
            u"incorrect", u"expired", u"string", u"redacted", u"your_password", u"your_api_key"
        };
        
        static constexpr std::u16string_view FILE_EXTENSIONS[] = {
            u".cs", u".xaml", u".json", u".png", u".jpg", u".jpeg", u".exe", u".dll", u".md", u".html", u".txt",
            u".ts", u".tsx", u".js", u".jsx", u".py", u".zip", u".pdf", u".docx", u".xlsx", u".csv", u".xml",
            u".yaml", u".yml", u".log", u".sql", u".css", u".svg", u".mp4", u".mp3"
        };
        
        static inline std::u16string_view get_file_name_without_ext(std::u16string_view path) noexcept
        {
            size_t last_sep = std::u16string_view::npos;
            for (size_t i = 0; i < path.size(); ++i)
            {
                if (path[i] == u'\\' || path[i] == u'/' || path[i] == u':')
                {
                    last_sep = i;
                }
            }
            std::u16string_view name = (last_sep == std::u16string_view::npos) ? path : path.substr(last_sep + 1);
            size_t last_dot = name.rfind(u'.');
            return (last_dot == std::u16string_view::npos) ? name : name.substr(0, last_dot);
        }
        
        static inline std::u16string_view get_path_ext(std::u16string_view path) noexcept
        {
            for (size_t i = path.size(); i > 0; --i)
            {
                char16_t c = path[i - 1];
                if (c == u'.')
                {
                    return (i - 1 > 0 && path[i - 2] != u'\\' && path[i - 2] != u'/' && path[i - 2] != u':')
                        ? path.substr(i - 1)
                        : std::u16string_view{};
                }
                if (c == u'\\' || c == u'/' || c == u':')
                {
                    break;
                }
            }
            return {};
        }
        
        static inline bool is_ascii_hex(char16_t c) noexcept
        {
            return (c >= u'0' && c <= u'9') || (c >= u'a' && c <= u'f') || (c >= u'A' && c <= u'F');
        }

        static inline int32_t hex_value(char16_t c) noexcept
        {
            if (c >= u'0' && c <= u'9') return c - u'0';
            if (c >= u'a' && c <= u'f') return 10 + (c - u'a');
            if (c >= u'A' && c <= u'F') return 10 + (c - u'A');
            return -1;
        }

        static inline bool is_label_sep(char16_t c) noexcept
        {
            return c == u'_' || c == u'-' || c == u' ';
        }

        static inline bool is_label_prefix_char(char16_t c) noexcept
        {
            return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z') ||
                   (c >= u'0' && c <= u'9') || c == u'_' || c == u'.' || c == u'-';
        }

        static inline bool matches_compound_word(std::u16string_view text, std::u16string_view first, std::u16string_view second) noexcept
        {
            if (!starts_with_ignore_case(text, first))
            {
                return false;
            }
            std::u16string_view rem = text.substr(first.size());
            if (!rem.empty() && is_label_sep(rem.front()))
            {
                rem.remove_prefix(1);
            }
            return equals_ignore_case(rem, second);
        }

        static inline bool is_secret_suffix(std::u16string_view rem) noexcept
        {
            if (rem.empty())
            {
                return true;
            }
            if (is_label_sep(rem.front()))
            {
                rem.remove_prefix(1);
            }
            if (starts_with_ignore_case(rem, u"access"))
            {
                rem.remove_prefix(6);
                if (rem.empty())
                {
                    return true;
                }
                if (is_label_sep(rem.front()))
                {
                    rem.remove_prefix(1);
                }
            }
            static constexpr std::u16string_view SECRET_SUFFIXES[] = {
                u"key", u"token", u"value", u"id", u"code", u"string", u"hash"
            };
            for (std::u16string_view suf : SECRET_SUFFIXES)
            {
                if (equals_ignore_case(rem, suf))
                {
                    return true;
                }
            }
            return false;
        }

        static inline bool is_sensitive_label_tail(std::u16string_view tail) noexcept
        {
            static constexpr std::u16string_view EXACT_LABELS[] = {
                u"password", u"passwd", u"pwd", u"passphrase", u"passcode", u"pass", u"pin",
                u"apikey", u"token", u"otp", u"totp", u"2fa"
            };
            for (std::u16string_view exact : EXACT_LABELS)
            {
                if (equals_ignore_case(tail, exact))
                {
                    return true;
                }
            }
            if (starts_with_ignore_case(tail, u"secret") && is_secret_suffix(tail.substr(6)))
            {
                return true;
            }
            return matches_compound_word(tail, u"private", u"key") ||
                   matches_compound_word(tail, u"secret", u"key") ||
                   matches_compound_word(tail, u"access", u"key") ||
                   matches_compound_word(tail, u"api", u"key") ||
                   matches_compound_word(tail, u"access", u"token") ||
                   matches_compound_word(tail, u"refresh", u"token") ||
                   matches_compound_word(tail, u"session", u"token") ||
                   matches_compound_word(tail, u"auth", u"token") ||
                   matches_compound_word(tail, u"id", u"token") ||
                   matches_compound_word(tail, u"client", u"secret") ||
                   matches_compound_word(tail, u"verification", u"code");
        }

        static bool is_sensitive_credential_label(std::u16string_view raw_label) noexcept
        {
            std::u16string_view label = trim_view(raw_label);
            if (label.size() >= 2 &&
                ((label.front() == u'"' && label.back() == u'"') ||
                 (label.front() == u'\'' && label.back() == u'\'')))
            {
                label = trim_view(label.substr(1, label.size() - 2));
            }
            if (label.empty())
            {
                return false;
            }
            if (is_sensitive_label_tail(label))
            {
                return true;
            }
            for (size_t i = 0; i < label.size(); ++i)
            {
                char16_t c = label[i];
                if (is_label_sep(c))
                {
                    if (i + 1 < label.size() && is_sensitive_label_tail(label.substr(i + 1)))
                    {
                        return true;
                    }
                }
                else if (!is_label_prefix_char(c))
                {
                    return false;
                }
            }
            return false;
        }

        static bool TryDecodeHexPayload(std::u16string_view text, char16_t* out_wide, int32_t out_cap, int32_t* out_len) noexcept
        {
            if (out_wide == nullptr || out_cap <= 0 || out_len == nullptr)
            {
                return false;
            }
            *out_len = 0;
            std::u16string_view trimmed = trim_view(text);
            if (trimmed.size() < 32)
            {
                return false;
            }

            std::u16string_view hex_span{};
            size_t i = 0;
            while (i < trimmed.size())
            {
                if (i + 2 < trimmed.size() && trimmed[i] == u'0' && (trimmed[i + 1] == u'x' || trimmed[i + 1] == u'X') && is_ascii_hex(trimmed[i + 2]))
                {
                    i += 2;
                }
                if (!is_ascii_hex(trimmed[i]))
                {
                    ++i;
                    continue;
                }
                size_t start = i;
                while (i < trimmed.size() && is_ascii_hex(trimmed[i]))
                {
                    ++i;
                }
                size_t run_len = i - start;
                if (run_len >= 32)
                {
                    hex_span = trimmed.substr(start, run_len & ~static_cast<size_t>(1));
                    break;
                }
            }
            if (hex_span.size() < 32)
            {
                return false;
            }
            uint8_t decoded_utf8[8192];
            wchar_t decoded_wide[8192];
            size_t decoded_len = std::min<size_t>(hex_span.size() / 2, 8192);
            if (decoded_len < 16)
            {
                return false;
            }
            for (size_t idx = 0; idx < decoded_len; ++idx)
            {
                int32_t hi = hex_value(hex_span[idx * 2]);
                int32_t lo = hex_value(hex_span[idx * 2 + 1]);
                if (hi < 0 || lo < 0)
                {
                    return false;
                }
                uint8_t b = static_cast<uint8_t>((hi << 4) | lo);
                decoded_utf8[idx] = (b == 0u) ? static_cast<uint8_t>(' ') : b;
            }
            int wide_len = MultiByteToWideChar(
                CP_UTF8,
                0,
                reinterpret_cast<const char*>(decoded_utf8),
                static_cast<int>(decoded_len),
                decoded_wide,
                8192);
            if (wide_len <= 0)
            {
                return false;
            }
            int32_t copy_len = std::min<int32_t>(wide_len, out_cap);
            std::memcpy(out_wide, decoded_wide, static_cast<size_t>(copy_len) * sizeof(char16_t));
            *out_len = copy_len;
            return true;
        }
        
        static inline int32_t classify_char_for_entropy(char16_t c, int32_t& lower, int32_t& upper, int32_t& digits, int32_t& symbols, bool& strong_sym) noexcept
        {
            if (c >= u'a' && c <= u'z')
            {
                ++lower;
                return 0;
            }
            if (c >= u'A' && c <= u'Z')
            {
                ++upper;
                return 1;
            }
            if (c >= u'0' && c <= u'9')
            {
                ++digits;
                return 2;
            }
            if (c >= 0x80u)
            {
                WORD ctype = 0;
                WCHAR wc = static_cast<WCHAR>(c);
                if (GetStringTypeW(CT_CTYPE1, &wc, 1, &ctype))
                {
                    if ((ctype & C1_LOWER) != 0) { ++lower; return 0; }
                    if ((ctype & C1_UPPER) != 0) { ++upper; return 1; }
                    if ((ctype & C1_DIGIT) != 0) { ++digits; return 2; }
                    if ((ctype & C1_ALPHA) != 0) { ++lower; return 0; }
                }
                if (vn_is_letter_or_digit(c))
                {
                    ++lower;
                    return 0;
                }
            }
            ++symbols;
            static constexpr std::u16string_view STRONG_SYMS = u"@#$%!^&*?~+=";
            if (STRONG_SYMS.find(c) != std::u16string_view::npos)
            {
                strong_sym = true;
            }
            return 3;
        }

        static int32_t vn_clipboard_sensitive_is_from_password_manager_impl(const char16_t* app_ptr, int32_t app_len,
    const char16_t* exe_ptr, int32_t exe_len,
    uint8_t* out_is_pm) noexcept
        {
    if (out_is_pm == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_pm = 0u;
    std::u16string_view exe = make_u16_view(exe_ptr, exe_len);
    std::u16string_view target = !is_null_or_whitespace(exe)
        ? get_file_name_without_ext(exe)
        : trim_view(make_u16_view(app_ptr, app_len));
    for (std::u16string_view pm : PM_NAMES)
    {
        if (equals_ignore_case(target, pm))
        {
            *out_is_pm = 1u;
            return VN_OK;
        }
    }
    return VN_OK;
}

        static int32_t vn_clipboard_sensitive_is_credential_value_impl(const char16_t* val_ptr, int32_t val_len,
            uint8_t* out_is_cred) noexcept
        {
            if (out_is_cred == nullptr)
            {
                return VN_ERR_INVALID_ARG;
            }
            *out_is_cred = 0u;
            std::u16string_view val = trim_view(make_u16_view(val_ptr, val_len));
            if (val.size() >= 2 &&
                ((val.front() == u'"' && val.back() == u'"') ||
                 (val.front() == u'\'' && val.back() == u'\'')))
            {
                val = trim_view(val.substr(1, val.size() - 2));
            }
            if (val.empty() || val.size() > 256)
            {
                return VN_OK;
            }
            if (val.front() == u'$' || val.front() == u'%' ||
                starts_with_ignore_case(val, u"process.env.") ||
                starts_with_ignore_case(val, u"os.environ") ||
                starts_with_ignore_case(val, u"env.") ||
                starts_with_ignore_case(val, u"system.environment"))
            {
                return VN_OK;
            }
            for (char16_t c : val)
            {
                if (c == u'\r' || c == u'\n' || c == u'<' || c == u'>' || c == u'{' || c == u'}')
                {
                    return VN_OK;
                }
            }
            for (std::u16string_view nv : NON_VALUES)
            {
                if (equals_ignore_case(val, nv))
                {
                    return VN_OK;
                }
            }
            bool all_mask = true;
            for (char16_t c : val)
            {
                if (c != u'*' && c != 0x2022u)
                {
                    all_mask = false;
                    break;
                }
            }
            if (all_mask)
            {
                return VN_OK;
            }
            *out_is_cred = 1u;
            return VN_OK;
        }

        static int32_t vn_clipboard_sensitive_luhn_check_impl(const char16_t* match_ptr, int32_t match_len,
    uint8_t* out_valid) noexcept
        {
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = 0u;
    std::u16string_view sv = make_u16_view(match_ptr, match_len);
    int32_t digits[19]{};
    int32_t count = 0;
    for (char16_t c : sv)
    {
        if (c >= u'0' && c <= u'9')
        {
            if (count >= 19)
            {
                return VN_OK;
            }
            digits[count++] = c - u'0';
        }
    }
    if (count < 13)
    {
        return VN_OK;
    }
    bool repeated = true;
    int32_t sum = 0;
    for (int32_t i = count - 1, pos = 0; i >= 0; --i, ++pos)
    {
        repeated = repeated && (digits[i] == digits[0]);
        int32_t n = digits[i];
        if ((pos % 2) == 1)
        {
            n *= 2;
            if (n > 9) n -= 9;
        }
        sum += n;
    }
    if (!repeated && (sum % 10) == 0)
    {
        *out_valid = 1u;
    }
    return VN_OK;
}

        static int32_t vn_clipboard_sensitive_analyze_standalone_impl(const char16_t* text_ptr, int32_t text_len,
    uint8_t is_guid_or_email_or_version,
    uint8_t is_identifier,
    int32_t* out_reason,
    int32_t* out_confidence) noexcept
        {
    if (out_reason == nullptr || out_confidence == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_reason = 0; // None
    *out_confidence = 0; // None

    std::u16string_view text = make_u16_view(text_ptr, text_len);
    if (text.size() < 8 || text.size() > 256 || is_guid_or_email_or_version != 0)
    {
        return VN_OK;
    }
    for (char16_t c : text)
    {
        if (is_unicode_whitespace(c))
        {
            return VN_OK;
        }
    }

    if (contains_ordinal(text, u"://") ||
        starts_with_ignore_case(text, u"mailto:") ||
        text[0] == u'/' || text[0] == u'\\' ||
        starts_with_ordinal(text, u"~/") ||
        starts_with_ordinal(text, u"./") ||
        starts_with_ordinal(text, u"../") ||
        (text.size() > 2 && ((text[0] >= u'a' && text[0] <= u'z') || (text[0] >= u'A' && text[0] <= u'Z')) &&
         text[1] == u':' && (text[2] == u'\\' || text[2] == u'/')))
    {
        return VN_OK;
    }

    std::u16string_view ext = get_path_ext(text);
    if (!ext.empty())
    {
        for (std::u16string_view fe : FILE_EXTENSIONS)
        {
            if (equals_ignore_case(ext, fe))
            {
                return VN_OK;
            }
        }
    }

    if (text[0] == u'#' && (text.size() == 4 || text.size() == 7 || text.size() == 9))
    {
        bool all_hex = true;
        for (size_t i = 1; i < text.size(); ++i)
        {
            if (!is_ascii_hex(text[i])) { all_hex = false; break; }
        }
        if (all_hex) return VN_OK;
    }

    if (text.size() == 32 || text.size() == 40 || text.size() == 64)
    {
        bool all_hex = true;
        for (char16_t c : text)
        {
            if (!is_ascii_hex(c)) { all_hex = false; break; }
        }
        if (all_hex) return VN_OK;
    }

    size_t eq_pos = text.find(u'=');
    if (eq_pos != std::u16string_view::npos && eq_pos > 0 && eq_pos + 1 < text.size() &&
        !contains_ordinal(text, u"=>") && !contains_ordinal(text, u"==") && !contains_ordinal(text, u"!="))
    {
        std::u16string_view lhs = text.substr(0, eq_pos);
        std::u16string_view rhs = text.substr(eq_pos + 1);
        uint8_t is_cred = 0u;
        if (is_sensitive_credential_label(lhs) &&
            vn_clipboard_sensitive_is_credential_value_impl(rhs.data(), static_cast<int32_t>(rhs.size()), &is_cred) == VN_OK &&
            is_cred != 0u)
        {
            *out_reason = 3; // CredentialLabel
            *out_confidence = 3; // High
            return VN_OK;
        }
    }
    if (contains_ordinal(text, u"=>") ||
        contains_ordinal(text, u"==") ||
        contains_ordinal(text, u"!=") ||
        contains_ordinal(text, u"::") ||
        (eq_pos != std::u16string_view::npos && eq_pos < text.size() - 2) ||
        text.find(u'(') != std::u16string_view::npos ||
        text.find(u')') != std::u16string_view::npos ||
        text.find(u'{') != std::u16string_view::npos ||
        text.find(u'}') != std::u16string_view::npos ||
        text.find(u';') != std::u16string_view::npos)
    {
        *out_reason = 7; // Ambiguous
        *out_confidence = 1; // Possible
        return VN_OK;
    }

    int32_t lower = 0, upper = 0, digits = 0, symbols = 0, transitions = 0, previous = -1;
    bool strong_sym = false;

    char16_t unique_chars[256]{};
    int32_t counts[256]{};
    size_t unique_len = 0;

    for (char16_t c : text)
    {
        int32_t cat = classify_char_for_entropy(c, lower, upper, digits, symbols, strong_sym);
        if (previous >= 0 && previous != cat)
        {
            ++transitions;
        }
        previous = cat;

        bool found = false;
        for (size_t u = 0; u < unique_len; ++u)
        {
            if (unique_chars[u] == c)
            {
                ++counts[u];
                found = true;
                break;
            }
        }
        if (!found && unique_len < 256)
        {
            unique_chars[unique_len] = c;
            counts[unique_len] = 1;
            ++unique_len;
        }
    }

    double entropy = 0.0;
    double inv_len = 1.0 / static_cast<double>(text.size());
    for (size_t u = 0; u < unique_len; ++u)
    {
        double p = static_cast<double>(counts[u]) * inv_len;
        entropy -= p * std::log2(p);
    }

    bool decorated = strong_sym && (lower + upper > 0) && (digits > 0 || (lower > 0 && upper > 0));
    bool generated = (symbols == 0) && lower >= 2 && upper >= 2 && digits >= 2 && text.size() >= 10 &&
                     transitions >= 5 &&
                     (static_cast<double>(transitions) / static_cast<double>(text.size() - 1)) >= 0.45 &&
                     entropy >= 3.0;
    if (decorated || generated)
    {
        *out_reason = 6; // StandalonePassword
        *out_confidence = 2; // Likely
        return VN_OK;
    }
    if (symbols > 0 || digits > 0 || is_identifier != 0)
    {
        *out_reason = 7; // Ambiguous
        *out_confidence = 1; // Possible
        return VN_OK;
    }

    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_clipboard_sensitive_is_from_password_manager(const char16_t* app_ptr, int32_t app_len,
    const char16_t* exe_ptr, int32_t exe_len,
    uint8_t* out_is_pm) noexcept
{
    try
    {
        return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_is_from_password_manager_impl(app_ptr, app_len, exe_ptr, exe_len, out_is_pm);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_sensitive_is_credential_value(const char16_t* val_ptr, int32_t val_len,
    uint8_t* out_is_cred) noexcept
{
    try
    {
        return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_is_credential_value_impl(val_ptr, val_len, out_is_cred);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_sensitive_luhn_check(const char16_t* match_ptr, int32_t match_len,
    uint8_t* out_valid) noexcept
{
    try
    {
        return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_luhn_check_impl(match_ptr, match_len, out_valid);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_sensitive_analyze_standalone(const char16_t* text_ptr, int32_t text_len,
    uint8_t is_guid_or_email_or_version,
    uint8_t is_identifier,
    int32_t* out_reason,
    int32_t* out_confidence) noexcept
{
    try
    {
        return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_analyze_standalone_impl(text_ptr, text_len, is_guid_or_email_or_version, is_identifier, out_reason, out_confidence);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_sensitive_try_decode_hex_payload(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_wide, int32_t out_cap,
    int32_t* out_len, uint8_t* out_decoded) noexcept
{
    try
    {
        if (out_wide == nullptr || out_cap <= 0 || out_len == nullptr || out_decoded == nullptr)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_len = 0;
        *out_decoded = vnotch::services::ClipboardSensitiveData::TryDecodeHexPayload(
            vnotch::make_u16_view(text_ptr, text_len), out_wide, out_cap, out_len) ? 1u : 0u;
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}


