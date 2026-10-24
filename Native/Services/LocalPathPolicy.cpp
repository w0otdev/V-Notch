#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class LocalPathPolicy final
    {
    public:
        static int32_t vn_local_path_is_unc_impl(const char16_t* path_ptr, int32_t path_len, uint8_t* out_is_unc) noexcept
        {
    if (out_is_unc == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(path_ptr, path_len);
    if (is_null_or_whitespace(sv))
    {
        *out_is_unc = 0u;
        return VN_OK;
    }
    if (starts_with_ordinal(sv, u"\\\\") || starts_with_ordinal(sv, u"//"))
    {
        *out_is_unc = 1u;
        return VN_OK;
    }
    std::u16string_view trimmed = trim_view(sv);
    if (starts_with_ignore_case(trimmed, u"file://"))
    {
        std::u16string_view rest = trimmed.substr(7);
        if (starts_with_ordinal(rest, u"//") || starts_with_ordinal(rest, u"\\\\"))
        {
            *out_is_unc = 1u;
            return VN_OK;
        }
        if (!rest.empty() && rest[0] != u'/' && rest[0] != u'\\')
        {
            // file://host/share -> UNC unless drive letter or localhost
            bool is_drive = rest.size() >= 2 &&
                            ((rest[0] >= u'A' && rest[0] <= u'Z') || (rest[0] >= u'a' && rest[0] <= u'z')) &&
                            (rest[1] == u':' || rest[1] == u'|');
            if (!is_drive && !starts_with_ignore_case(rest, u"localhost/"))
            {
                *out_is_unc = 1u;
                return VN_OK;
            }
        }
    }
    *out_is_unc = 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_local_path_is_unc(const char16_t* path_ptr, int32_t path_len, uint8_t* out_is_unc) noexcept
{
    return vnotch::services::LocalPathPolicy::vn_local_path_is_unc_impl(path_ptr, path_len, out_is_unc);
}

