#include "vnotch_common.h"

namespace vnotch::services
{

class TranslationModelStore final
{
public:
    static bool IsAllowedDownloadUri(
        std::u16string_view scheme,
        int32_t user_info_len,
        uint8_t is_default_port,
        std::u16string_view host) noexcept
    {
        if (scheme != u"https" || user_info_len != 0 || is_default_port == 0)
        {
            return false;
        }
        return host == u"huggingface.co" ||
               vnotch::ends_with_ignore_case(host, u".huggingface.co") ||
               vnotch::ends_with_ignore_case(host, u".hf.co");
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_translation_model_store_is_allowed_uri(
    const char16_t* scheme_ptr, int32_t scheme_len,
    int32_t user_info_len,
    uint8_t is_default_port,
    const char16_t* host_ptr, int32_t host_len,
    uint8_t* out_allowed) noexcept
{
    if (out_allowed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_allowed = vnotch::services::TranslationModelStore::IsAllowedDownloadUri(
        vnotch::make_u16_view(scheme_ptr, scheme_len),
        user_info_len,
        is_default_port,
        vnotch::make_u16_view(host_ptr, host_len)) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
