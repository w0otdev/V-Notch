#include "vnotch_common.h"

namespace vnotch::services
{

class StartupGreeting final
{
public:
    static constexpr bool UsesVietnamese(std::u16string_view language) noexcept
    {
        std::u16string_view trimmed = vnotch::trim_view(language);
        return trimmed.size() == 2 &&
               (trimmed[0] == u'v' || trimmed[0] == u'V') &&
               (trimmed[1] == u'i' || trimmed[1] == u'I');
    }

    static constexpr bool UsesEnglishHandwriting(std::u16string_view language) noexcept
    {
        return !UsesVietnamese(language);
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_startup_greeting_uses_vietnamese(
    const char16_t* lang_ptr,
    int32_t lang_len,
    uint8_t* out_result) noexcept
{
    if (out_result == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_result = vnotch::services::StartupGreeting::UsesVietnamese(vnotch::make_u16_view(lang_ptr, lang_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_startup_greeting_uses_english_handwriting(
    const char16_t* lang_ptr,
    int32_t lang_len,
    uint8_t* out_result) noexcept
{
    if (out_result == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_result = vnotch::services::StartupGreeting::UsesEnglishHandwriting(vnotch::make_u16_view(lang_ptr, lang_len)) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
