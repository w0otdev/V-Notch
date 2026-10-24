#include "vnotch_common.h"
#include <array>
#include <cstdint>
#include <string>
#include <string_view>

namespace vnotch::services {

class TranslationModelPrompts final {
public:
    static constexpr bool IsHunyuanLanguage(std::u16string_view code) noexcept {
        constexpr std::array<std::u16string_view, 37> kHunyuanLanguages = {
            u"zh", u"en", u"fr", u"pt", u"es", u"ja", u"tr", u"ru", u"ar", u"ko",
            u"th", u"it", u"de", u"vi", u"ms", u"id", u"tl", u"hi", u"pl", u"cs",
            u"nl", u"km", u"my", u"fa", u"gu", u"ur", u"te", u"mr", u"he", u"bn",
            u"ta", u"uk", u"bo", u"kk", u"mn", u"ug", u"yue"
        };
        for (auto lang : kHunyuanLanguages) {
            if (lang == code) return true;
        }
        return false;
    }

    static constexpr bool IsHunyuanSupported(std::u16string_view source, std::u16string_view target) noexcept {
        if (!IsHunyuanLanguage(target)) return false;
        if (source != u"auto" && !IsHunyuanLanguage(source)) return false;
        return true;
    }

    static std::u16string SanitizeControlTokens(std::u16string_view text) {
        std::u16string res(text);
        vnotch::replace_all_ordinal(res, u"<|", u"< |");
        vnotch::replace_all_ordinal(res, u"<start_of_turn>", u"< start_of_turn >");
        vnotch::replace_all_ordinal(res, u"<end_of_turn>", u"< end_of_turn >");
        vnotch::replace_all_ordinal(res, u"<bos>", u"< bos >");
        return res;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_transprompts_is_hunyuan_supported(
    const char16_t* source, int32_t source_len,
    const char16_t* target, int32_t target_len,
    uint8_t* out_supported) noexcept
{
    if (!out_supported) return VN_ERR_INVALID_ARG;
    *out_supported = vnotch::services::TranslationModelPrompts::IsHunyuanSupported(
        vnotch::make_u16_view(source, source_len),
        vnotch::make_u16_view(target, target_len)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_transprompts_sanitize_control_tokens(
    const char16_t* text, int32_t text_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    try {
        auto res = vnotch::services::TranslationModelPrompts::SanitizeControlTokens(
            vnotch::make_u16_view(text, text_len));
        return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
