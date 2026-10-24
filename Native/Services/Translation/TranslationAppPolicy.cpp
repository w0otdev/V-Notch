#include "vnotch_common.h"
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <string>
#include <string_view>

namespace vnotch::services {

class TranslationAppPolicy final {
public:
    static std::u16string_view NormalizeName(std::u16string_view name) noexcept {
        if (name.size() >= 4) {
            std::u16string_view suffix = name.substr(name.size() - 4);
            if (suffix[0] == u'.' &&
                ToLowerAscii(suffix[1]) == u'e' &&
                ToLowerAscii(suffix[2]) == u'x' &&
                ToLowerAscii(suffix[3]) == u'e') {
                return name.substr(0, name.size() - 4);
            }
        }
        return name;
    }

    static std::u16string ParseAppsJoined(std::u16string_view apps) {
        std::u16string out;
        size_t i = 0;
        size_t n = apps.size();
        while (i < n) {
            while (i < n && IsSeparator(apps[i])) i++;
            size_t start = i;
            while (i < n && !IsSeparator(apps[i])) i++;
            std::u16string_view token = Trim(apps.substr(start, i - start));
            token = NormalizeName(token);
            if (!token.empty()) {
                if (!out.empty()) out.push_back(u'\n');
                out.append(token);
            }
        }
        return out;
    }

private:
    static constexpr char16_t ToLowerAscii(char16_t c) noexcept {
        return (c >= u'A' && c <= u'Z') ? static_cast<char16_t>(c + 32) : c;
    }

    static constexpr bool IsSeparator(char16_t c) noexcept {
        return c == u';' || c == u',' || c == u'\r' || c == u'\n';
    }

    static bool IsWhiteSpace(char16_t c) noexcept {
        return std::iswspace(static_cast<wint_t>(c)) != 0;
    }

    static std::u16string_view Trim(std::u16string_view s) noexcept {
        size_t start = 0;
        size_t end = s.size();
        while (start < end && IsWhiteSpace(s[start])) start++;
        while (end > start && IsWhiteSpace(s[end - 1])) end--;
        return s.substr(start, end - start);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_translation_app_normalize_name(
    const char16_t* name_ptr, int32_t name_len,
    int32_t* out_trimmed_len) noexcept
{
    if (!out_trimmed_len) return VN_ERR_INVALID_ARG;
    if (!name_ptr || name_len <= 0) {
        *out_trimmed_len = 0;
        return VN_OK;
    }
    auto norm = vnotch::services::TranslationAppPolicy::NormalizeName(
        std::u16string_view(name_ptr, static_cast<size_t>(name_len)));
    *out_trimmed_len = static_cast<int32_t>(norm.size());
    return VN_OK;
}

VN_EXPORT int32_t vn_translation_app_parse_joined(
    const char16_t* apps_ptr, int32_t apps_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    if (!apps_ptr || apps_len <= 0) return VN_OK;
    try {
        std::u16string res = vnotch::services::TranslationAppPolicy::ParseAppsJoined(
            std::u16string_view(apps_ptr, static_cast<size_t>(apps_len)));
        int32_t req = static_cast<int32_t>(res.size());
        *out_len = req;
        if (req == 0) return VN_OK;
        if (!out_buf || out_cap < req) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_buf, res.data(), static_cast<size_t>(req) * sizeof(char16_t));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
