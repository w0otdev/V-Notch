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
            if (val.size() < 6 || val.size() > 256)
            {
                return VN_OK;
            }
            for (char16_t c : val)
            {
                if (is_unicode_whitespace(c) || c == u'<' || c == u'>' || c == u'{' || c == u'}')
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
    return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_is_from_password_manager_impl(app_ptr, app_len, exe_ptr, exe_len, out_is_pm);
}

VN_EXPORT int32_t vn_clipboard_sensitive_is_credential_value(const char16_t* val_ptr, int32_t val_len,
    uint8_t* out_is_cred) noexcept
{
    return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_is_credential_value_impl(val_ptr, val_len, out_is_cred);
}

VN_EXPORT int32_t vn_clipboard_sensitive_luhn_check(const char16_t* match_ptr, int32_t match_len,
    uint8_t* out_valid) noexcept
{
    return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_luhn_check_impl(match_ptr, match_len, out_valid);
}

VN_EXPORT int32_t vn_clipboard_sensitive_analyze_standalone(const char16_t* text_ptr, int32_t text_len,
    uint8_t is_guid_or_email_or_version,
    uint8_t is_identifier,
    int32_t* out_reason,
    int32_t* out_confidence) noexcept
{
    return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_analyze_standalone_impl(text_ptr, text_len, is_guid_or_email_or_version, is_identifier, out_reason, out_confidence);
}

