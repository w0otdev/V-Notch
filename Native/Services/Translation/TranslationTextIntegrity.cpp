#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class TranslationTextIntegrity final
    {
    public:
        static bool is_word_char(char16_t c) noexcept
            {
                return vnotch::is_unicode_letter_or_digit(c) || c == u'_';
            }
        
            static bool is_digit_char(char16_t c) noexcept
            {
                return vnotch::is_ascii_digit(c);
            }

        static int32_t vn_translation_normalize_fact_impl(const char16_t* fact_ptr, int32_t fact_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    std::u16string_view fact = vnotch::make_view(fact_ptr, fact_len);

    std::u16string out;
    out.reserve(fact.size());

    for (size_t i = 0; i < fact.size(); ++i)
    {
        char16_t c = fact[i];
        if (c == u'.' || c == u',')
        {
            // Check (?<=\b\d{1,3})
            size_t d_before = 0;
            while (d_before < 4 && i > d_before && is_digit_char(fact[i - 1 - d_before]))
                ++d_before;
            bool valid_before = (d_before >= 1 && d_before <= 3) &&
                                (i == d_before || !is_word_char(fact[i - 1 - d_before]));

            // Check (?=\d{3}(?:\D|$))
            bool valid_after = (i + 3 < fact.size()) &&
                               is_digit_char(fact[i + 1]) &&
                               is_digit_char(fact[i + 2]) &&
                               is_digit_char(fact[i + 3]) &&
                               (i + 4 == fact.size() || !is_digit_char(fact[i + 4]));

            if (valid_before && valid_after)
                continue;
        }
        out.push_back(c);
    }

    return vnotch::write_u16_out(out, out_buf, out_cap, out_len);
}

        static int32_t vn_translation_has_unresolved_placeholder_impl(const char16_t* source_ptr, int32_t source_len,
    const char16_t* translated_ptr, int32_t translated_len,
    uint8_t* out_result)
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    *out_result = 0;

    std::u16string_view source = vnotch::make_view(source_ptr, source_len);
    std::u16string_view translated = vnotch::make_view(translated_ptr, translated_len);

    constexpr std::u16string_view kPrefix = u"__VNT_";
    size_t pos = 0;
    while (pos < translated.size())
    {
        size_t idx = translated.find(kPrefix, pos);
        if (idx == std::u16string_view::npos) break;

        size_t end = idx + kPrefix.size();
        while (end < translated.size() && is_word_char(translated[end]))
            ++end;

        if (end > idx + kPrefix.size())
        {
            std::u16string_view token = translated.substr(idx, end - idx);
            if (source.find(token) == std::u16string_view::npos)
            {
                *out_result = 1;
                return VN_OK;
            }
            pos = end;
        }
        else
        {
            pos = idx + kPrefix.size();
        }
    }

    return VN_OK;
}

        static int32_t vn_translation_clean_text_impl(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    return vnotch::write_u16_out(vnotch::trim(vnotch::make_view(text_ptr, text_len)), out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_translation_normalize_fact(const char16_t* fact_ptr, int32_t fact_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::TranslationTextIntegrity::vn_translation_normalize_fact_impl(fact_ptr, fact_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_translation_has_unresolved_placeholder(const char16_t* source_ptr, int32_t source_len,
    const char16_t* translated_ptr, int32_t translated_len,
    uint8_t* out_result) noexcept
{
    try { return vnotch::services::TranslationTextIntegrity::vn_translation_has_unresolved_placeholder_impl(source_ptr, source_len, translated_ptr, translated_len, out_result);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_translation_clean_text(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::TranslationTextIntegrity::vn_translation_clean_text_impl(text_ptr, text_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

