#include "vnotch_common.h"

namespace vnotch::services
{
    class MediaProgressHelpers final
    {
    public:
        static bool HasLogicalSessionChanged(
            std::u16string_view last_app,
            std::u16string_view last_key,
            std::u16string_view cur_app,
            std::u16string_view cur_key) noexcept
        {
            if (!is_null_or_whitespace(last_key) && !is_null_or_whitespace(cur_key))
            {
                return !equals_ordinal(last_key, cur_key);
            }
            return !is_null_or_whitespace(last_app) &&
                   !is_null_or_whitespace(cur_app) &&
                   !equals_ignore_case(last_app, cur_app);
        }
    };
} // namespace vnotch::services

using namespace vnotch;
using namespace vnotch::services;

VN_EXPORT int32_t vn_media_progress_has_logical_session_changed(
    const char16_t* last_app_ptr, int32_t last_app_len,
    const char16_t* last_key_ptr, int32_t last_key_len,
    const char16_t* cur_app_ptr, int32_t cur_app_len,
    const char16_t* cur_key_ptr, int32_t cur_key_len,
    uint8_t* out_changed) noexcept
{
    if (out_changed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_changed = MediaProgressHelpers::HasLogicalSessionChanged(
        make_u16_view(last_app_ptr, last_app_len),
        make_u16_view(last_key_ptr, last_key_len),
        make_u16_view(cur_app_ptr, cur_app_len),
        make_u16_view(cur_key_ptr, cur_key_len)) ? 1u : 0u;
    return VN_OK;
}
