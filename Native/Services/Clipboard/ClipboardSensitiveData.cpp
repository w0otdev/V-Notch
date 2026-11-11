#include "vnotch_common.h"
#include "../../vnotch_keyword_trie.h"

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
            static constexpr auto suffixes = vnotch::MakeKeywordTrie(SECRET_SUFFIXES);
            return suffixes.Contains(rem);
        }

        static inline bool is_sensitive_label_tail(std::u16string_view tail) noexcept
        {
            static constexpr std::u16string_view EXACT_LABELS[] = {
                u"password", u"passwd", u"pwd", u"passphrase", u"passcode", u"pass", u"pin",
                u"apikey", u"token", u"otp", u"totp", u"2fa"
            };
            static constexpr auto labels = vnotch::MakeKeywordTrie(EXACT_LABELS);
            if (labels.Contains(tail)) return true;
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

        static inline bool is_ascii_digit(char16_t c) noexcept
        {
            return c >= u'0' && c <= u'9';
        }

        static inline bool is_ascii_alpha(char16_t c) noexcept
        {
            return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z');
        }

        static inline bool is_ascii_alnum(char16_t c) noexcept
        {
            return is_ascii_alpha(c) || is_ascii_digit(c);
        }

        static inline bool is_ascii_alnum_under(char16_t c) noexcept
        {
            return is_ascii_alnum(c) || c == u'_';
        }

        static inline bool is_alnum_dash_under(char16_t c) noexcept
        {
            return is_ascii_alnum(c) || c == u'_' || c == u'-';
        }

        static inline bool is_bearer_char(char16_t c) noexcept
        {
            return is_ascii_alnum(c) || c == u'.' || c == u'_' || c == u'~' || c == u'+' || c == u'/' || c == u'-';
        }

        static inline bool is_regex_word_char(char16_t c) noexcept
        {
            if (is_ascii_alnum(c) || c == u'_') return true;
            if (c < 0x80u) return false;
            return vn_is_letter_or_digit(c);
        }

        static inline bool is_word_boundary(std::u16string_view s, size_t pos) noexcept
        {
            const bool left = (pos > 0) && is_regex_word_char(s[pos - 1]);
            const bool right = (pos < s.size()) && is_regex_word_char(s[pos]);
            return left != right;
        }

        template <typename Pred>
        static inline bool match_greedy_with_word_boundary(
            std::u16string_view s,
            size_t start,
            size_t min_len,
            size_t max_len,
            Pred pred,
            size_t* out_end = nullptr) noexcept
        {
            size_t end = start;
            const size_t limit = (max_len == static_cast<size_t>(-1) || start + max_len > s.size())
                ? s.size()
                : (start + max_len);
            while (end < limit && pred(s[end]))
            {
                ++end;
            }
            while (end >= start + min_len)
            {
                if (is_word_boundary(s, end))
                {
                    if (out_end) *out_end = end;
                    return true;
                }
                --end;
            }
            return false;
        }

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

        __declspec(noinline) static bool VerifyJwtSegments(
            std::u16string_view p0,
            std::u16string_view p1,
            std::u16string_view p2) noexcept
        {
            uint8_t header_buf[12288];
            uint8_t payload_buf[12288];
            uint8_t sig_buf[12288];
            size_t h_len = 0, p_len = 0, s_len = 0;
            return decode_base64url(p0, header_buf, sizeof(header_buf), h_len) &&
                   decode_base64url(p1, payload_buf, sizeof(payload_buf), p_len) &&
                   decode_base64url(p2, sig_buf, sizeof(sig_buf), s_len) &&
                   s_len > 0 &&
                   is_valid_json_object(header_buf, h_len, true) &&
                   is_valid_json_object(payload_buf, p_len, false);
        }

        static bool ContainsJwt(std::u16string_view text) noexcept
        {
            if (text.size() < 10) return false;
            size_t i = 0;
            while (i + 8 < text.size())
            {
                if ((text[i] == u'e' || text[i] == u'E') &&
                    (text[i + 1] == u'y' || text[i + 1] == u'Y') &&
                    (text[i + 2] == u'j' || text[i + 2] == u'J') &&
                    is_word_boundary(text, i))
                {
                    size_t p0_end = i + 3;
                    while (p0_end < text.size() && is_alnum_dash_under(text[p0_end])) ++p0_end;
                    if (p0_end > i + 3 && p0_end + 1 < text.size() && text[p0_end] == u'.')
                    {
                        const size_t p1_start = p0_end + 1;
                        size_t p1_end = p1_start;
                        while (p1_end < text.size() && is_alnum_dash_under(text[p1_end])) ++p1_end;
                        if (p1_end > p1_start && p1_end + 1 < text.size() && text[p1_end] == u'.')
                        {
                            const size_t p2_start = p1_end + 1;
                            size_t p2_end = p2_start;
                            if (match_greedy_with_word_boundary(text, p2_start, 1, static_cast<size_t>(-1), is_alnum_dash_under, &p2_end))
                            {
                                const size_t total_len = p2_end - i;
                                if (total_len <= 16384 &&
                                    VerifyJwtSegments(
                                        text.substr(i, p0_end - i),
                                        text.substr(p1_start, p1_end - p1_start),
                                        text.substr(p2_start, p2_end - p2_start)))
                                {
                                    return true;
                                }
                                i = p2_end;
                                continue;
                            }
                        }
                    }
                }
                ++i;
            }
            return false;
        }

        static bool MatchesEmailCredentialWhole(std::u16string_view text) noexcept
        {
            if (text.size() < 7) return false;
            size_t i = 0;
            while (i < text.size() && (is_regex_word_char(text[i]) || text[i] == u'.' || text[i] == u'+' || text[i] == u'-')) ++i;
            if (i == 0 || i >= text.size() || text[i] != u'@') return false;
            ++i;
            const size_t dom1_start = i;
            while (i < text.size() && (is_regex_word_char(text[i]) || text[i] == u'-')) ++i;
            if (i == dom1_start || i >= text.size() || text[i] != u'.') return false;
            ++i;
            const size_t dom2_start = i;
            while (i < text.size() && (is_regex_word_char(text[i]) || text[i] == u'.' || text[i] == u'-')) ++i;
            if (i == dom2_start || i >= text.size() || text[i] != u':') return false;
            ++i;
            if (i >= text.size()) return false;
            while (i < text.size())
            {
                if (is_unicode_whitespace(text[i])) return false;
                ++i;
            }
            return true;
        }

        static bool ContainsCredentialFormat(std::u16string_view text) noexcept
        {
            if (MatchesEmailCredentialWhole(text)) return true;

            const size_t n = text.size();
            for (size_t i = 0; i < n; ++i)
            {
                const char16_t c = text[i];

                // 1. Private Keys: -----BEGIN (?:[A-Z0-9_-]+ )*PRIVATE KEY(?: BLOCK)?-----
                if (c == u'-' && i + 27 <= n && starts_with_ignore_case(text.substr(i), u"-----BEGIN "))
                {
                    size_t p = i + 11;
                    while (p < n)
                    {
                        const std::u16string_view rem = text.substr(p);
                        if (starts_with_ignore_case(rem, u"PRIVATE KEY-----") ||
                            starts_with_ignore_case(rem, u"PRIVATE KEY BLOCK-----"))
                        {
                            return true;
                        }
                        size_t w = p;
                        while (w < n && is_alnum_dash_under(text[w])) ++w;
                        if (w == p || w >= n || text[w] != u' ') break;
                        p = w + 1;
                    }
                }

                // 9. WIFI:(?:[^;]*;)*P:[^;]+;
                if ((c == u'W' || c == u'w') && i + 8 <= n && starts_with_ignore_case(text.substr(i), u"WIFI:"))
                {
                    size_t p = i + 5;
                    while (p < n)
                    {
                        size_t semi = p;
                        while (semi < n && text[semi] != u';') ++semi;
                        if (semi >= n) break;
                        const std::u16string_view seg = text.substr(p, semi - p);
                        if (seg.size() >= 3 && (seg[0] == u'P' || seg[0] == u'p') && seg[1] == u':')
                        {
                            return true;
                        }
                        p = semi + 1;
                    }
                }

                if (!is_word_boundary(text, i)) continue;
                const std::u16string_view rem = text.substr(i);

                // 2. \b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,})\b
                if (rem.size() >= 23 && starts_with_ignore_case(rem, u"sk-"))
                {
                    size_t body = i + 3;
                    if (starts_with_ignore_case(text.substr(body), u"proj-")) body += 5;
                    else if (starts_with_ignore_case(text.substr(body), u"ant-")) body += 4;
                    if (match_greedy_with_word_boundary(text, body, 20, static_cast<size_t>(-1), is_alnum_dash_under))
                    {
                        return true;
                    }
                }

                // 3a. gh[pousr]_[A-Za-z0-9]{20,}\b
                if (rem.size() >= 24 && (rem[0] == u'g' || rem[0] == u'G') && (rem[1] == u'h' || rem[1] == u'H') && rem[3] == u'_')
                {
                    const char16_t t = (rem[2] >= u'A' && rem[2] <= u'Z') ? static_cast<char16_t>(rem[2] + 32) : rem[2];
                    if ((t == u'p' || t == u'o' || t == u'u' || t == u's' || t == u'r') &&
                        match_greedy_with_word_boundary(text, i + 4, 20, static_cast<size_t>(-1), is_ascii_alnum))
                    {
                        return true;
                    }
                }

                // 3b. github_pat_[A-Za-z0-9_]{20,}\b
                if (rem.size() >= 31 && starts_with_ignore_case(rem, u"github_pat_") &&
                    match_greedy_with_word_boundary(text, i + 11, 20, static_cast<size_t>(-1), is_ascii_alnum_under))
                {
                    return true;
                }

                // 3c. AKIA[A-Z0-9]{16}\b
                if (rem.size() >= 20 && starts_with_ignore_case(rem, u"AKIA") &&
                    match_greedy_with_word_boundary(text, i + 4, 16, 16, is_ascii_alnum))
                {
                    return true;
                }

                // 4a. AIza[0-9A-Za-z_-]{35}\b
                if (rem.size() >= 39 && starts_with_ignore_case(rem, u"AIza") &&
                    match_greedy_with_word_boundary(text, i + 4, 35, 35, is_alnum_dash_under))
                {
                    return true;
                }

                // 4b. xox[baprs]-[0-9A-Za-z]{10,48}\b
                if (rem.size() >= 15 && starts_with_ignore_case(rem, u"xox") && rem[4] == u'-')
                {
                    const char16_t t = (rem[3] >= u'A' && rem[3] <= u'Z') ? static_cast<char16_t>(rem[3] + 32) : rem[3];
                    if ((t == u'b' || t == u'a' || t == u'p' || t == u'r' || t == u's') &&
                        match_greedy_with_word_boundary(text, i + 5, 10, 48, is_ascii_alnum))
                    {
                        return true;
                    }
                }

                // 5a. [rs]k_(?:live|test)_[0-9a-zA-Z]{24,}\b
                if (rem.size() >= 32 &&
                    (rem[0] == u'r' || rem[0] == u'R' || rem[0] == u's' || rem[0] == u'S') &&
                    (rem[1] == u'k' || rem[1] == u'K') && rem[2] == u'_' &&
                    (starts_with_ignore_case(rem.substr(3), u"live_") || starts_with_ignore_case(rem.substr(3), u"test_")) &&
                    match_greedy_with_word_boundary(text, i + 8, 24, static_cast<size_t>(-1), is_ascii_alnum))
                {
                    return true;
                }

                // 5b. SG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}\b
                if (rem.size() >= 69 && starts_with_ignore_case(rem, u"SG."))
                {
                    size_t p1 = i + 3;
                    while (p1 < n && p1 < i + 25 && is_alnum_dash_under(text[p1])) ++p1;
                    if (p1 == i + 25 && p1 < n && text[p1] == u'.' &&
                        match_greedy_with_word_boundary(text, p1 + 1, 43, 43, is_alnum_dash_under))
                    {
                        return true;
                    }
                }

                // 6. \b[MN][A-Za-z0-9]{23,}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,}\b
                if (rem.size() >= 59 && (rem[0] == u'M' || rem[0] == u'm' || rem[0] == u'N' || rem[0] == u'n'))
                {
                    size_t p1 = i + 1;
                    while (p1 < n && is_ascii_alnum(text[p1])) ++p1;
                    if (p1 - (i + 1) >= 23 && p1 + 8 < n && text[p1] == u'.')
                    {
                        size_t p2 = p1 + 1;
                        while (p2 < n && p2 < p1 + 7 && is_alnum_dash_under(text[p2])) ++p2;
                        if (p2 == p1 + 7 && p2 < n && text[p2] == u'.' &&
                            match_greedy_with_word_boundary(text, p2 + 1, 27, static_cast<size_t>(-1), is_alnum_dash_under))
                        {
                            return true;
                        }
                    }
                }

                // 7. \bBearer\s+[A-Za-z0-9._~+/-]{20,}\b
                if (rem.size() >= 27 && starts_with_ignore_case(rem, u"Bearer") && is_unicode_whitespace(rem[6]))
                {
                    size_t p = i + 7;
                    while (p < n && is_unicode_whitespace(text[p])) ++p;
                    if (match_greedy_with_word_boundary(text, p, 20, static_cast<size_t>(-1), is_bearer_char))
                    {
                        return true;
                    }
                }

                // 8. \b[a-zA-Z][a-zA-Z0-9+.-]*://[^/\s:@]*:[^/\s:@]+@[^/\s:@]+
                if (is_ascii_alpha(c))
                {
                    size_t p = i + 1;
                    while (p < n && (is_ascii_alnum(text[p]) || text[p] == u'+' || text[p] == u'.' || text[p] == u'-')) ++p;
                    if (p + 3 < n && text[p] == u':' && text[p + 1] == u'/' && text[p + 2] == u'/')
                    {
                        p += 3;
                        auto is_url_cred_char = [](char16_t ch) noexcept {
                            return ch != u'/' && ch != u':' && ch != u'@' && !is_unicode_whitespace(ch);
                        };
                        while (p < n && is_url_cred_char(text[p])) ++p;
                        if (p < n && text[p] == u':')
                        {
                            ++p;
                            const size_t pw_start = p;
                            while (p < n && is_url_cred_char(text[p])) ++p;
                            if (p > pw_start && p + 1 < n && text[p] == u'@' && is_url_cred_char(text[p + 1]))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        static bool TryMatchLabelValueAt(std::u16string_view text, size_t pos) noexcept
        {
            const size_t n = text.size();
            if (pos < n && (text[pos] == u'"' || text[pos] == u'\''))
            {
                ++pos;
            }
            while (pos < n && is_unicode_whitespace(text[pos])) ++pos;
            if (pos >= n) return false;

            size_t after_op = static_cast<size_t>(-1);
            if (pos + 2 <= n && ((text[pos] == u'=' && text[pos + 1] == u'>') || (text[pos] == u'-' && text[pos + 1] == u'>')))
            {
                after_op = pos + 2;
            }
            else if (text[pos] == u':' || text[pos] == u'=')
            {
                after_op = pos + 1;
            }
            else if (pos + 2 <= n && is_word_boundary(text, pos) && is_word_boundary(text, pos + 2))
            {
                const std::u16string_view op = text.substr(pos, 2);
                if (equals_ignore_case(op, u"is") || equals_ignore_case(op, u"la") || equals_ignore_case(op, u"l\u00E0"))
                {
                    after_op = pos + 2;
                }
            }
            if (after_op == static_cast<size_t>(-1)) return false;

            pos = after_op;
            while (pos < n && is_unicode_whitespace(text[pos])) ++pos;
            if (pos >= n) return false;

            std::u16string_view val{};
            if (text[pos] == u'"')
            {
                size_t close = pos + 1;
                while (close < n && text[close] != u'"' && text[close] != u'\r' && text[close] != u'\n') ++close;
                if (close >= n || text[close] != u'"' || close == pos + 1) return false;
                val = text.substr(pos + 1, close - (pos + 1));
            }
            else if (text[pos] == u'\'')
            {
                size_t close = pos + 1;
                while (close < n && text[close] != u'\'' && text[close] != u'\r' && text[close] != u'\n') ++close;
                if (close >= n || text[close] != u'\'' || close == pos + 1) return false;
                val = text.substr(pos + 1, close - (pos + 1));
            }
            else
            {
                size_t end_val = pos;
                while (end_val < n)
                {
                    const char16_t c = text[end_val];
                    if (is_unicode_whitespace(c) || c == u'"' || c == u'\'' || c == u';' ||
                        c == u',' || c == u'}' || c == u'<' || c == u'>')
                    {
                        break;
                    }
                    ++end_val;
                }
                if (end_val == pos) return false;
                val = text.substr(pos, end_val - pos);
            }

            uint8_t is_cred = 0u;
            return vn_clipboard_sensitive_is_credential_value_impl(val.data(), static_cast<int32_t>(val.size()), &is_cred) == VN_OK && is_cred != 0u;
        }

        static bool HasEnglishLabelPrefixBoundary(std::u16string_view text, size_t kw_pos) noexcept
        {
            if (is_word_boundary(text, kw_pos)) return true;
            if (kw_pos == 0 || text[kw_pos - 1] != u'_') return false;
            size_t p = kw_pos - 1;
            while (p > 0)
            {
                const char16_t c = text[p - 1];
                if (is_ascii_alnum(c) || c == u'_' || c == u'.' || c == u'-')
                {
                    --p;
                    if (is_word_boundary(text, p)) return true;
                }
                else
                {
                    break;
                }
            }
            return is_word_boundary(text, p);
        }

        static bool MatchCompoundAt(
            std::u16string_view rem,
            std::u16string_view first,
            std::u16string_view second,
            bool allow_hyphen,
            size_t& out_len) noexcept
        {
            if (!starts_with_ignore_case(rem, first)) return false;
            size_t pos = first.size();
            if (pos < rem.size())
            {
                const char16_t sep = rem[pos];
                if (sep == u'_' || sep == u' ' || (allow_hyphen && sep == u'-'))
                {
                    ++pos;
                }
            }
            if (!starts_with_ignore_case(rem.substr(pos), second)) return false;
            out_len = pos + second.size();
            return true;
        }

        static bool ContainsCredentialLabel(std::u16string_view text) noexcept
        {
            const size_t n = text.size();
            for (size_t i = 0; i < n; ++i)
            {
                const char16_t c0 = to_lower_ascii(text[i]);
                if (c0 != u's' && c0 != u'p' && c0 != u'a' && c0 != u'c' &&
                    c0 != u'r' && c0 != u'i' && c0 != u'v' && c0 != u't' &&
                    c0 != u'o' && c0 != u'2' && c0 != u'm')
                {
                    continue;
                }
                const std::u16string_view rem = text.substr(i);

                // English labels
                if (c0 != u'm' && HasEnglishLabelPrefixBoundary(text, i))
                {
                    // secret(?:[_ -]?access)?(?:[_ -]?(?:key|token|value|id|code|string|hash))?
                    if (c0 == u's' && starts_with_ignore_case(rem, u"secret"))
                    {
                        size_t base_len = 6;
                        if (TryMatchLabelValueAt(text, i + base_len)) return true;

                        size_t after_access = base_len;
                        size_t p = base_len;
                        if (p < rem.size() && is_label_sep(rem[p])) ++p;
                        if (starts_with_ignore_case(rem.substr(p), u"access"))
                        {
                            after_access = p + 6;
                            if (TryMatchLabelValueAt(text, i + after_access)) return true;
                        }

                        static constexpr std::u16string_view kSecretSuffixes[] = {
                            u"key", u"token", u"value", u"id", u"code", u"string", u"hash"
                        };
                        const size_t starts[2] = { after_access, base_len };
                        const int start_cnt = (after_access != base_len) ? 2 : 1;
                        for (int s_idx = 0; s_idx < start_cnt; ++s_idx)
                        {
                            size_t sp = starts[s_idx];
                            if (sp < rem.size() && is_label_sep(rem[sp])) ++sp;
                            for (std::u16string_view suf : kSecretSuffixes)
                            {
                                if (starts_with_ignore_case(rem.substr(sp), suf) &&
                                    TryMatchLabelValueAt(text, i + sp + suf.size()))
                                {
                                    return true;
                                }
                            }
                        }
                    }

                    struct CompoundPair { std::u16string_view first; std::u16string_view second; };
                    static constexpr CompoundPair kEnglishCompounds[] = {
                        { u"private", u"key" }, { u"access", u"key" }, { u"api", u"key" },
                        { u"client", u"secret" }, { u"auth", u"token" }, { u"access", u"token" },
                        { u"refresh", u"token" }, { u"session", u"token" }, { u"id", u"token" },
                        { u"verification", u"code" }
                    };
                    for (const auto& cp : kEnglishCompounds)
                    {
                        if (cp.first[0] != c0) continue;
                        size_t clen = 0;
                        if (MatchCompoundAt(rem, cp.first, cp.second, true, clen) &&
                            TryMatchLabelValueAt(text, i + clen))
                        {
                            return true;
                        }
                    }

                    static constexpr std::u16string_view kEnglishSimple[] = {
                        u"passphrase", u"password", u"passcode", u"passwd", u"apikey",
                        u"token", u"totp", u"pass", u"pwd", u"pin", u"otp", u"2fa"
                    };
                    for (std::u16string_view kw : kEnglishSimple)
                    {
                        if (kw[0] != c0) continue;
                        if (starts_with_ignore_case(rem, kw) && TryMatchLabelValueAt(text, i + kw.size()))
                        {
                            return true;
                        }
                    }
                }

                // Vietnamese labels (require word boundary at i, separators [_ ]?)
                if (c0 == u'm' && is_word_boundary(text, i))
                {
                    struct CompoundPair { std::u16string_view first; std::u16string_view second; };
                    static constexpr CompoundPair kVnCompounds[] = {
                        { u"m\u1EADt", u"kh\u1EA9u" }, { u"mat", u"khau" },
                        { u"m\u1EADt", u"m\u00E3" }, { u"mat", u"ma" },
                        { u"m\u00E3", u"pin" }, { u"ma", u"pin" },
                        { u"m\u00E3", u"otp" }, { u"ma", u"otp" }
                    };
                    for (const auto& cp : kVnCompounds)
                    {
                        size_t clen = 0;
                        if (MatchCompoundAt(rem, cp.first, cp.second, false, clen) &&
                            TryMatchLabelValueAt(text, i + clen))
                        {
                            return true;
                        }
                    }

                    if (starts_with_ignore_case(rem, u"mk") && TryMatchLabelValueAt(text, i + 2))
                    {
                        return true;
                    }

                    struct TripleWord { std::u16string_view w1; std::u16string_view w2; std::u16string_view w3; };
                    static constexpr TripleWord kVnTriples[] = {
                        { u"m\u00E3", u"x\u00E1c", u"nh\u1EADn" },
                        { u"m\u00E3", u"x\u00E1c", u"th\u1EF1c" },
                        { u"ma", u"xac", u"nhan" },
                        { u"ma", u"xac", u"thuc" },
                        { u"m\u00E3", u"b\u1EA3o", u"m\u1EADt" },
                        { u"ma", u"bao", u"mat" }
                    };
                    for (const auto& tr : kVnTriples)
                    {
                        size_t p12 = 0;
                        if (MatchCompoundAt(rem, tr.w1, tr.w2, false, p12))
                        {
                            size_t p = p12;
                            if (p < rem.size() && (rem[p] == u'_' || rem[p] == u' ')) ++p;
                            if (starts_with_ignore_case(rem.substr(p), tr.w3) &&
                                TryMatchLabelValueAt(text, i + p + tr.w3.size()))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        static bool ContainsContextualCode(std::u16string_view text) noexcept
        {
            static constexpr std::u16string_view kContextPhrases[] = {
                u"otp",
                u"m\u00E3 otp", u"ma otp",
                u"m\u00E3 x\u00E1c nh\u1EADn", u"ma xac nhan",
                u"m\u00E3 x\u00E1c th\u1EF1c", u"ma xac thuc",
                u"verification code", u"security code"
            };
            const size_t n = text.size();
            for (size_t i = 0; i < n; ++i)
            {
                if (!is_word_boundary(text, i)) continue;
                const std::u16string_view rem = text.substr(i);
                for (std::u16string_view phrase : kContextPhrases)
                {
                    if (starts_with_ignore_case(rem, phrase) && is_word_boundary(text, i + phrase.size()))
                    {
                        const size_t start_scan = i + phrase.size();
                        for (size_t offset = 0; offset <= 35 && start_scan + offset < n; ++offset)
                        {
                            const size_t pos = start_scan + offset;
                            const char16_t ch = text[pos];
                            if (ch == u'\r' || ch == u'\n') break;
                            if (is_ascii_digit(ch) && is_word_boundary(text, pos))
                            {
                                if (match_greedy_with_word_boundary(text, pos, 4, 8, is_ascii_digit))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        static bool ContainsPaymentCard(std::u16string_view text) noexcept
        {
            const size_t n = text.size();
            for (size_t i = 0; i < n; ++i)
            {
                if (!is_ascii_digit(text[i]) || !is_word_boundary(text, i)) continue;
                size_t digit_ends[19]{};
                int32_t dcount = 1;
                digit_ends[0] = i + 1;
                size_t p = i + 1;
                while (dcount < 19 && p < n)
                {
                    size_t next_d = p;
                    if (text[next_d] == u' ' || text[next_d] == u'-') ++next_d;
                    if (next_d < n && is_ascii_digit(text[next_d]))
                    {
                        digit_ends[dcount++] = next_d + 1;
                        p = next_d + 1;
                    }
                    else
                    {
                        break;
                    }
                }
                for (int32_t k = dcount - 1; k >= 12; --k)
                {
                    const size_t end_pos = digit_ends[k];
                    if (is_word_boundary(text, end_pos))
                    {
                        uint8_t valid = 0u;
                        const std::u16string_view span = text.substr(i, end_pos - i);
                        if (vn_clipboard_sensitive_luhn_check_impl(span.data(), static_cast<int32_t>(span.size()), &valid) == VN_OK && valid != 0u)
                        {
                            return true;
                        }
                        break;
                    }
                }
            }
            return false;
        }

        static bool IsStandaloneVerificationCode(std::u16string_view candidate) noexcept
        {
            const size_t n = candidate.size();
            if (n >= 6 && n <= 8)
            {
                bool all_digits = true;
                for (char16_t c : candidate)
                {
                    if (!is_ascii_digit(c)) { all_digits = false; break; }
                }
                if (all_digits) return true;
            }
            if (n == 7 && (candidate[3] == u' ' || candidate[3] == u'-') &&
                is_ascii_digit(candidate[0]) && is_ascii_digit(candidate[1]) && is_ascii_digit(candidate[2]) &&
                is_ascii_digit(candidate[4]) && is_ascii_digit(candidate[5]) && is_ascii_digit(candidate[6]))
            {
                return true;
            }
            if (n == 8 && (candidate[0] == u'G' || candidate[0] == u'g') && candidate[1] == u'-')
            {
                bool all_digits = true;
                for (size_t i = 2; i < 8; ++i)
                {
                    if (!is_ascii_digit(candidate[i])) { all_digits = false; break; }
                }
                if (all_digits) return true;
            }
            return false;
        }

        static bool IsGuidCandidate(std::u16string_view s) noexcept
        {
            if (s.size() == 38 &&
                ((s.front() == u'{' && s.back() == u'}') || (s.front() == u'(' && s.back() == u')')))
            {
                s = s.substr(1, 36);
            }
            if (s.size() == 32)
            {
                for (char16_t c : s)
                {
                    if (!is_ascii_hex(c)) return false;
                }
                return true;
            }
            if (s.size() == 36)
            {
                for (size_t i = 0; i < 36; ++i)
                {
                    if (i == 8 || i == 13 || i == 18 || i == 23)
                    {
                        if (s[i] != u'-') return false;
                    }
                    else if (!is_ascii_hex(s[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        static bool IsEmailCandidate(std::u16string_view s) noexcept
        {
            if (s.size() < 5) return false;
            size_t i = 0;
            while (i < s.size() && (is_regex_word_char(s[i]) || s[i] == u'.' || s[i] == u'+' || s[i] == u'-')) ++i;
            if (i == 0 || i >= s.size() || s[i] != u'@') return false;
            ++i;
            const size_t dom_start = i;
            while (i < s.size() && (is_regex_word_char(s[i]) || s[i] == u'-')) ++i;
            if (i == dom_start || i >= s.size() || s[i] != u'.') return false;
            while (i < s.size())
            {
                if (s[i] != u'.') return false;
                ++i;
                const size_t seg_start = i;
                while (i < s.size() && (is_regex_word_char(s[i]) || s[i] == u'-')) ++i;
                if (i == seg_start) return false;
            }
            return true;
        }

        static inline bool is_unicode_digit_char(char16_t c) noexcept
        {
            if (is_ascii_digit(c)) return true;
            if (c < 0x80u) return false;
            WORD ctype = 0;
            WCHAR wc = static_cast<WCHAR>(c);
            return GetStringTypeW(CT_CTYPE1, &wc, 1, &ctype) && (ctype & C1_DIGIT) != 0;
        }

        static bool IsVersionCandidate(std::u16string_view s) noexcept
        {
            if (s.empty()) return false;
            size_t i = 0;
            if (s[0] == u'v' || s[0] == u'V') ++i;
            const size_t d0 = i;
            while (i < s.size() && is_unicode_digit_char(s[i])) ++i;
            if (i == d0) return false;
            int dots = 0;
            while (i < s.size() && s[i] == u'.' && dots < 3)
            {
                ++i;
                const size_t ds = i;
                while (i < s.size() && is_unicode_digit_char(s[i])) ++i;
                if (i == ds) return false;
                ++dots;
            }
            if (dots < 1) return false;
            if (i < s.size())
            {
                if (s[i] != u'-' && s[i] != u'+') return false;
                ++i;
                if (i >= s.size()) return false;
                while (i < s.size())
                {
                    if (!is_regex_word_char(s[i]) && s[i] != u'.' && s[i] != u'-') return false;
                    ++i;
                }
            }
            return true;
        }

        static bool IsIdentifierCandidate(std::u16string_view s) noexcept
        {
            if (s.empty()) return false;
            const char16_t first = s[0];
            if (is_unicode_digit_char(first)) return false;
            if (!is_regex_word_char(first)) return false;
            for (size_t i = 1; i < s.size(); ++i)
            {
                if (!is_regex_word_char(s[i])) return false;
            }
            return true;
        }

        static bool HasInvalidSurrogates(std::u16string_view s) noexcept
        {
            for (size_t i = 0; i < s.size(); ++i)
            {
                const char16_t c = s[i];
                if (c >= 0xD800u && c <= 0xDBFFu)
                {
                    if (i + 1 >= s.size() || s[i + 1] < 0xDC00u || s[i + 1] > 0xDFFFu) return true;
                    ++i;
                }
                else if (c >= 0xDC00u && c <= 0xDFFFu)
                {
                    return true;
                }
            }
            return false;
        }

        static bool HasHexRunAtLeast32(std::u16string_view text) noexcept
        {
            size_t run = 0;
            for (size_t i = 0; i < text.size(); ++i)
            {
                if (is_ascii_hex(text[i]))
                {
                    if (++run >= 32) return true;
                }
                else
                {
                    run = 0;
                }
            }
            return false;
        }

        __declspec(noinline) static bool InspectHexPayload(std::u16string_view normalized, int32_t& out_reason, int32_t& out_confidence) noexcept
        {
            char16_t hex_decoded[8192];
            int32_t decoded_len = 0;
            if (!TryDecodeHexPayload(normalized, hex_decoded, 8192, &decoded_len) || decoded_len <= 0)
            {
                return false;
            }
            const std::u16string_view decoded_sv(hex_decoded, static_cast<size_t>(decoded_len));
            if (ContainsJwt(decoded_sv) || ContainsCredentialFormat(decoded_sv))
            {
                RtlSecureZeroMemory(hex_decoded, sizeof(hex_decoded));
                out_reason = 2; // CredentialFormat
                out_confidence = 3; // High
                return true;
            }
            if (ContainsCredentialLabel(decoded_sv))
            {
                RtlSecureZeroMemory(hex_decoded, sizeof(hex_decoded));
                out_reason = 3; // CredentialLabel
                out_confidence = 3; // High
                return true;
            }
            RtlSecureZeroMemory(hex_decoded, sizeof(hex_decoded));
            return false;
        }

        static void InspectNormalizedText(std::u16string_view normalized, int32_t& out_reason, int32_t& out_confidence) noexcept
        {
            if (ContainsJwt(normalized) || ContainsCredentialFormat(normalized))
            {
                out_reason = 2; // CredentialFormat
                out_confidence = 3; // High
                return;
            }
            if (ContainsCredentialLabel(normalized))
            {
                out_reason = 3; // CredentialLabel
                out_confidence = 3; // High
                return;
            }
            if (normalized.size() >= 32 && HasHexRunAtLeast32(normalized) &&
                InspectHexPayload(normalized, out_reason, out_confidence))
            {
                return;
            }
            size_t total_digits = 0;
            size_t cur_digit_run = 0;
            size_t max_digit_run = 0;
            for (char16_t ch : normalized)
            {
                if (is_ascii_digit(ch))
                {
                    ++total_digits;
                    if (++cur_digit_run > max_digit_run) max_digit_run = cur_digit_run;
                }
                else
                {
                    cur_digit_run = 0;
                }
            }
            if (max_digit_run >= 4 && ContainsContextualCode(normalized))
            {
                out_reason = 4; // VerificationCode
                out_confidence = 3; // High
                return;
            }
            if (total_digits >= 13 && ContainsPaymentCard(normalized))
            {
                out_reason = 5; // PaymentCard
                out_confidence = 3; // High
                return;
            }
            const std::u16string_view candidate = trim_view(normalized);
            if (IsStandaloneVerificationCode(candidate))
            {
                out_reason = 4; // VerificationCode
                out_confidence = 2; // Likely
                return;
            }
            if (candidate.size() >= 8 && candidate.size() <= 256)
            {
                const bool is_guid_email_ver = IsGuidCandidate(candidate) || IsEmailCandidate(candidate) || IsVersionCandidate(candidate);
                const bool is_ident = !is_guid_email_ver && IsIdentifierCandidate(candidate);
                (void)vn_clipboard_sensitive_analyze_standalone_impl(
                    candidate.data(),
                    static_cast<int32_t>(candidate.size()),
                    is_guid_email_ver ? 1u : 0u,
                    is_ident ? 1u : 0u,
                    &out_reason,
                    &out_confidence);
            }
        }

        static int32_t vn_clipboard_sensitive_analyze_impl(
            const char16_t* text_ptr, int32_t text_len,
            const char16_t* app_ptr, int32_t app_len,
            const char16_t* exe_ptr, int32_t exe_len,
            int32_t* out_reason, int32_t* out_confidence) noexcept
        {
            if (out_reason == nullptr || out_confidence == nullptr || text_len < 0 || app_len < 0 || exe_len < 0)
            {
                return VN_ERR_INVALID_ARG;
            }
            *out_reason = 0;
            *out_confidence = 0;

            const std::u16string_view raw = make_u16_view(text_ptr, text_len);
            if (is_null_or_whitespace(raw))
            {
                return VN_OK;
            }
            uint8_t is_pm = 0u;
            if (vn_clipboard_sensitive_is_from_password_manager_impl(app_ptr, app_len, exe_ptr, exe_len, &is_pm) == VN_OK && is_pm != 0u)
            {
                *out_reason = 1; // PasswordManager
                *out_confidence = 3; // High
                return VN_OK;
            }
            if (raw.size() > 1'000'000u || HasInvalidSurrogates(raw))
            {
                *out_reason = 8; // InspectionLimit
                *out_confidence = 3; // High
                return VN_OK;
            }

            bool has_combining_range = false;
            for (char16_t c : raw)
            {
                if (c >= 0x0300u) { has_combining_range = true; break; }
            }

            if (has_combining_range &&
                !IsNormalizedString(NormalizationC, reinterpret_cast<LPCWSTR>(raw.data()), static_cast<int>(raw.size())))
            {
                const int est = NormalizeString(NormalizationC, reinterpret_cast<LPCWSTR>(raw.data()), static_cast<int>(raw.size()), nullptr, 0);
                if (est <= 0)
                {
                    *out_reason = 8;
                    *out_confidence = 3;
                    return VN_OK;
                }
                if (est <= 4096)
                {
                    wchar_t norm_stack[4096];
                    const int actual = NormalizeString(NormalizationC, reinterpret_cast<LPCWSTR>(raw.data()), static_cast<int>(raw.size()), norm_stack, 4096);
                    if (actual <= 0)
                    {
                        *out_reason = 8;
                        *out_confidence = 3;
                        return VN_OK;
                    }
                    InspectNormalizedText(std::u16string_view(reinterpret_cast<const char16_t*>(norm_stack), static_cast<size_t>(actual)), *out_reason, *out_confidence);
                    RtlSecureZeroMemory(norm_stack, sizeof(norm_stack));
                    return VN_OK;
                }
                std::vector<wchar_t> norm_vec(static_cast<size_t>(est) + 16);
                const int actual = NormalizeString(NormalizationC, reinterpret_cast<LPCWSTR>(raw.data()), static_cast<int>(raw.size()), norm_vec.data(), static_cast<int>(norm_vec.size()));
                if (actual <= 0)
                {
                    *out_reason = 8;
                    *out_confidence = 3;
                    return VN_OK;
                }
                InspectNormalizedText(std::u16string_view(reinterpret_cast<const char16_t*>(norm_vec.data()), static_cast<size_t>(actual)), *out_reason, *out_confidence);
                RtlSecureZeroMemory(norm_vec.data(), norm_vec.size() * sizeof(wchar_t));
                return VN_OK;
            }

            InspectNormalizedText(raw, *out_reason, *out_confidence);
            return VN_OK;
        }

    };
} // namespace vnotch::services

extern "C" VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_analyze(
    const char16_t* text_ptr, int32_t text_len,
    const char16_t* app_ptr, int32_t app_len,
    const char16_t* exe_ptr, int32_t exe_len,
    int32_t* out_reason, int32_t* out_confidence) noexcept
{
    try
    {
        return vnotch::services::ClipboardSensitiveData::vn_clipboard_sensitive_analyze_impl(
            text_ptr, text_len, app_ptr, app_len, exe_ptr, exe_len, out_reason, out_confidence);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

extern "C" VN_EXPORT int32_t __stdcall vn_clipboard_sensitive_format_mask(
    const char16_t* text_ptr, int32_t text_len,
    char16_t* out_mask, int32_t out_cap,
    int32_t* out_len) noexcept
{
    try
    {
        (void)text_ptr;
        if (!out_mask || !out_len || text_len < 0) return VN_ERR_INVALID_ARG;
        if (out_cap < 8) return VN_ERR_BUFFER_TOO_SMALL;
        for (int32_t i = 0; i < 8; ++i)
        {
            out_mask[i] = u'\u2022';
        }
        *out_len = 8;
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}



