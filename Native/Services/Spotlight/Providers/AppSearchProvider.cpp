#include "vnotch_common.h"

namespace vnotch::services
{

class AppSearchProvider final
{
public:
    // Returns: 0 = Empty, 1 = UsePathDirectly, 2 = PrefixAppsFolderWithAppId, 3 = PrefixAppsFolderWithPath
    static int32_t ClassifyShellTarget(
        std::u16string_view path,
        std::u16string_view app_id,
        uint8_t is_unc,
        uint8_t exists_on_disk) noexcept
    {
        if (is_unc != 0)
        {
            return 0;
        }
        if (vnotch::starts_with_ignore_case(path, u"shell:") || exists_on_disk != 0)
        {
            return 1;
        }
        if (!app_id.empty())
        {
            return 2;
        }
        if (!path.empty())
        {
            return 3;
        }
        return 0;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_app_search_provider_classify_target(
    const char16_t* path_ptr, int32_t path_len,
    const char16_t* app_id_ptr, int32_t app_id_len,
    uint8_t is_unc,
    uint8_t exists_on_disk,
    int32_t* out_mode) noexcept
{
    if (out_mode == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_mode = vnotch::services::AppSearchProvider::ClassifyShellTarget(
        vnotch::make_u16_view(path_ptr, path_len),
        vnotch::make_u16_view(app_id_ptr, app_id_len),
        is_unc,
        exists_on_disk);
    return VN_OK;
}

} // extern "C"
