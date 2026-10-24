#include "vnotch_common.h"
#include <cstdint>
#include <string>
#include <string_view>

namespace vnotch::services {

class TranslationPhraseGlossary final {
public:
    static bool Find(
        std::u16string_view text,
        std::u16string_view source,
        std::u16string_view* out_source,
        std::u16string_view* out_meaning)
    {
        *out_source = {};
        *out_meaning = {};
        if (text.empty()) return false;

        std::u16string norm = vnotch::normalize_unicode_form(text, NormalizationC);
        std::u16string_view sv(norm);

        size_t start = 0;
        size_t end = sv.size();
        while (start < end && IsPunctOrSpace(sv[start])) start++;
        while (end > start && IsPunctOrSpace(sv[end - 1])) end--;

        std::u16string_view trimmed = sv.substr(start, end - start);
        if (trimmed.empty()) return false;

        std::u16string collapsed;
        collapsed.reserve(trimmed.size());
        bool prev_space = false;
        for (char16_t ch : trimmed) {
            if (IsSpace(ch)) {
                if (!prev_space) {
                    collapsed.push_back(u' ');
                    prev_space = true;
                }
            } else {
                collapsed.push_back(ch);
                prev_space = false;
            }
        }

        if (vnotch::equals_ignore_case(collapsed, u"yêu xa") &&
            (source == u"auto" || source == u"vi")) {
            *out_source = u"vi";
            *out_meaning = u"This expression means being in a romantic relationship while living far apart (a long-distance relationship). Preserve the geographic separation in the target language. It does not mean 'I love you' or 'I miss you'.";
            return true;
        }
        return false;
    }

private:
    static bool IsSpace(char16_t ch) noexcept {
        if (vnotch::is_unicode_whitespace(ch)) return true;
        wchar_t wc = static_cast<wchar_t>(ch);
        WORD ctype = 0;
        return GetStringTypeW(CT_CTYPE1, &wc, 1, &ctype) != FALSE && (ctype & C1_SPACE) != 0;
    }

    static bool IsPunctOrSpace(char16_t ch) noexcept {
        if (vnotch::is_unicode_whitespace(ch)) return true;
        wchar_t wc = static_cast<wchar_t>(ch);
        WORD ctype = 0;
        return GetStringTypeW(CT_CTYPE1, &wc, 1, &ctype) != FALSE && (ctype & (C1_SPACE | C1_PUNCT)) != 0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_translation_glossary_find(
    const char16_t* text_ptr, int32_t text_len,
    const char16_t* src_ptr, int32_t src_len,
    char16_t* out_src_buf, int32_t out_src_cap, int32_t* out_src_len,
    char16_t* out_meaning_buf, int32_t out_meaning_cap, int32_t* out_meaning_len,
    uint8_t* out_found) noexcept
{
    if (!out_src_len || !out_meaning_len || !out_found) return VN_ERR_INVALID_ARG;
    *out_src_len = 0;
    *out_meaning_len = 0;
    *out_found = 0;
    if (!text_ptr || text_len <= 0) return VN_OK;

    try {
        std::u16string_view text_v(text_ptr, static_cast<size_t>(text_len));
        std::u16string_view src_v = (src_ptr && src_len > 0)
            ? std::u16string_view(src_ptr, static_cast<size_t>(src_len))
            : u"auto";

        std::u16string_view res_src, res_meaning;
        if (!vnotch::services::TranslationPhraseGlossary::Find(text_v, src_v, &res_src, &res_meaning)) {
            return VN_OK;
        }

        *out_found = 1;
        int32_t rc1 = vnotch::copy_out_u16(res_src, out_src_buf, out_src_cap, out_src_len);
        int32_t rc2 = vnotch::copy_out_u16(res_meaning, out_meaning_buf, out_meaning_cap, out_meaning_len);
        if (rc1 != VN_OK) return rc1;
        return rc2;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
