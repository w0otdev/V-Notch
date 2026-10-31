#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class TranslationInput final
    {
    public:
        static int32_t vn_translation_input_sanitize_impl(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    std::u16string_view sv = vnotch::make_view(text_ptr, text_len);
    std::u16string out;
    out.reserve(sv.size());
    for (char16_t c : sv)
    {
        if (c != u'\0' && c != u'\uFFFC')
            out.push_back(c);
    }
    return vnotch::write_u16_out(out, out_buf, out_cap, out_len);
}

        static int32_t vn_translation_input_is_keyboard_mash_impl(const char16_t* text_ptr, int32_t text_len,
    uint8_t* out_result)
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    std::u16string_view sv = vnotch::trim(vnotch::make_view(text_ptr, text_len));
    if (sv.size() < 7 || sv.size() > 10)
    {
        *out_result = 0;
        return VN_OK;
    }

    constexpr std::u16string_view kMashes[] = {
        u"qwertyuiop", u"asdfghjkl", u"zxcvbnm",
        u"poiuytrewq", u"lkjhgfdsa", u"mnbvcxz"
    };
    for (std::u16string_view mash : kMashes)
    {
        if (vnotch::equals_icase(sv, mash))
        {
            *out_result = 1;
            return VN_OK;
        }
    }

    *out_result = 0;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_translation_input_sanitize(const char16_t* text_ptr, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::TranslationInput::vn_translation_input_sanitize_impl(text_ptr, text_len, out_buf, out_cap, out_len);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_translation_input_is_keyboard_mash(const char16_t* text_ptr, int32_t text_len,
    uint8_t* out_result) noexcept
{
    try { return vnotch::services::TranslationInput::vn_translation_input_is_keyboard_mash_impl(text_ptr, text_len, out_result);
 } catch (...) { return VN_ERR_FAIL; }
}

