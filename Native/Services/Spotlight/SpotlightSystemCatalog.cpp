#include "vnotch_common.h"

namespace vnotch::services
{

class SpotlightSystemCatalog final
{
public:
    static int32_t ExtractFallbackLength(std::u16string_view keywords) noexcept
    {
        size_t sep = keywords.find(u';');
        if (sep == std::u16string_view::npos)
        {
            return static_cast<int32_t>(keywords.size());
        }
        return static_cast<int32_t>(sep);
    }

    static bool ShouldUseFallbackLabel(std::u16string_view text, std::u16string_view key) noexcept
    {
        if (vnotch::trim_u16(text).empty())
        {
            return true;
        }
        return text == key;
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_spotlight_system_catalog_extract_fallback_len(
    const char16_t* keywords_ptr,
    int32_t keywords_len,
    int32_t* out_len) noexcept
{
    if (out_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_len = vnotch::services::SpotlightSystemCatalog::ExtractFallbackLength(
        vnotch::make_u16_view(keywords_ptr, keywords_len));
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_system_catalog_should_use_fallback(
    const char16_t* text_ptr,
    int32_t text_len,
    const char16_t* key_ptr,
    int32_t key_len,
    uint8_t* out_use_fallback) noexcept
{
    if (out_use_fallback == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_use_fallback = vnotch::services::SpotlightSystemCatalog::ShouldUseFallbackLabel(
        vnotch::make_u16_view(text_ptr, text_len),
        vnotch::make_u16_view(key_ptr, key_len)) ? 1u : 0u;
    return VN_OK;
}
