#include "vnotch_common.h"

namespace vnotch::services
{

class SafeLauncher final
{
public:
    // Returns: 0 = Reject, 1 = AllowedMsSettings, 2 = CheckStandardScheme
    static int32_t ClassifyUrlPrefix(std::u16string_view url) noexcept
    {
        if (vnotch::is_null_or_whitespace(url))
        {
            return 0;
        }
        if (vnotch::starts_with_ignore_case(url, u"ms-settings:"))
        {
            return vnotch::equals_ignore_case(url, u"ms-settings:batterysaver") ? 1 : 0;
        }
        return 2;
    }

    static bool IsAllowedScheme(std::u16string_view scheme, std::u16string_view original_string) noexcept
    {
        if (vnotch::equals_ignore_case(scheme, u"https") ||
            vnotch::equals_ignore_case(scheme, u"http") ||
            vnotch::equals_ignore_case(scheme, u"mailto"))
        {
            return true;
        }
        if (!original_string.empty() &&
            vnotch::equals_ignore_case(original_string, u"ms-settings:batterysaver"))
        {
            return true;
        }
        return false;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_safe_launcher_classify_url_prefix(
    const char16_t* url_ptr,
    int32_t url_len,
    int32_t* out_class) noexcept
{
    if (out_class == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_class = vnotch::services::SafeLauncher::ClassifyUrlPrefix(vnotch::make_u16_view(url_ptr, url_len));
    return VN_OK;
}

VN_EXPORT int32_t vn_safe_launcher_is_allowed_scheme(
    const char16_t* scheme_ptr,
    int32_t scheme_len,
    const char16_t* orig_ptr,
    int32_t orig_len,
    uint8_t* out_allowed) noexcept
{
    if (out_allowed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_allowed = vnotch::services::SafeLauncher::IsAllowedScheme(
        vnotch::make_u16_view(scheme_ptr, scheme_len),
        vnotch::make_u16_view(orig_ptr, orig_len)) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
