#include "vnotch_common.h"

namespace vnotch::services
{

class AppFileCleanupService final
{
public:
    static bool IsGuidName(std::u16string_view name) noexcept
    {
        if (name.size() != 32)
        {
            return false;
        }
        for (char16_t ch : name)
        {
            if (!vnotch::is_ascii_hex(ch))
            {
                return false;
            }
        }
        return true;
    }

    static bool IsAtomicTemporary(std::u16string_view name, std::u16string_view destination) noexcept
    {
        if (name.size() != destination.size() + 1 + 32 + 4)
        {
            return false;
        }
        if (!vnotch::starts_with_icase_ascii(name, destination))
        {
            return false;
        }
        if (name[destination.size()] != u'.')
        {
            return false;
        }
        if (!vnotch::ends_with_icase_ascii(name, u".tmp"))
        {
            return false;
        }
        return IsGuidName(name.substr(destination.size() + 1, 32));
    }

    static bool ShouldDeleteCandidate(
        int64_t age_ticks,
        int64_t min_age_ticks,
        int64_t retention_ticks,
        int64_t total_bytes,
        int64_t max_bytes) noexcept
    {
        if (age_ticks < min_age_ticks)
        {
            return false;
        }
        if (age_ticks < retention_ticks && total_bytes <= max_bytes)
        {
            return false;
        }
        return true;
    }

    static bool IsPathRelationProtected(std::u16string_view path, std::u16string_view active) noexcept
    {
        if (vnotch::equals_icase_win(path, active))
        {
            return true;
        }
        if (path.size() > active.size() &&
            path[active.size()] == u'\\' &&
            vnotch::equals_icase_win(path.substr(0, active.size()), active))
        {
            return true;
        }
        if (active.size() > path.size() &&
            active[path.size()] == u'\\' &&
            vnotch::equals_icase_win(active.substr(0, path.size()), path))
        {
            return true;
        }
        return false;
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_app_file_cleanup_is_guid_name(
    const char16_t* name_ptr,
    int32_t name_len,
    uint8_t* out_is_guid) noexcept
{
    if (out_is_guid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_guid = vnotch::services::AppFileCleanupService::IsGuidName(
        vnotch::make_u16_view(name_ptr, name_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_app_file_cleanup_is_atomic_temporary(
    const char16_t* name_ptr,
    int32_t name_len,
    const char16_t* dest_ptr,
    int32_t dest_len,
    uint8_t* out_is_temp) noexcept
{
    if (out_is_temp == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_temp = vnotch::services::AppFileCleanupService::IsAtomicTemporary(
        vnotch::make_u16_view(name_ptr, name_len),
        vnotch::make_u16_view(dest_ptr, dest_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_app_file_cleanup_should_delete_candidate(
    int64_t age_ticks,
    int64_t min_age_ticks,
    int64_t retention_ticks,
    int64_t total_bytes,
    int64_t max_bytes,
    uint8_t* out_should_delete) noexcept
{
    if (out_should_delete == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_should_delete = vnotch::services::AppFileCleanupService::ShouldDeleteCandidate(
        age_ticks, min_age_ticks, retention_ticks, total_bytes, max_bytes) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_app_file_cleanup_is_path_relation_protected(
    const char16_t* path_ptr,
    int32_t path_len,
    const char16_t* active_ptr,
    int32_t active_len,
    uint8_t* out_protected) noexcept
{
    if (out_protected == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_protected = vnotch::services::AppFileCleanupService::IsPathRelationProtected(
        vnotch::make_u16_view(path_ptr, path_len),
        vnotch::make_u16_view(active_ptr, active_len)) ? 1u : 0u;
    return VN_OK;
}
