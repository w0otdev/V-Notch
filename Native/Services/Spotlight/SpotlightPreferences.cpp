#include "vnotch_common.h"
#include <algorithm>

namespace vnotch::services
{

class SpotlightPreferences final
{
public:
    static bool HasValidFolderSyntax(std::u16string_view folder) noexcept
    {
        if (folder.empty())
        {
            return false;
        }
        for (char16_t c : folder)
        {
            if (c < 32u || c == u'\"' || c == u'<' || c == u'>' || c == u'|' || c == u'*' || c == u'?')
            {
                return false;
            }
        }
        return true;
    }

    static bool IsPathUnderFolder(std::u16string_view path, std::u16string_view folder) noexcept
    {
        if (path.empty() || folder.empty())
        {
            return false;
        }
        if (vnotch::equals_ignore_case(path, folder))
        {
            return true;
        }
        bool folder_ends_sep = folder.back() == u'\\' || folder.back() == u'/';
        if (folder_ends_sep)
        {
            return vnotch::starts_with_ignore_case(path, folder);
        }
        if (path.size() > folder.size() &&
            vnotch::starts_with_ignore_case(path, folder) &&
            (path[folder.size()] == u'\\' || path[folder.size()] == u'/'))
        {
            return true;
        }
        return false;
    }

    static double ComputePreferenceScore(
        double base_score,
        std::u16string_view normalized_query,
        std::u16string_view normalized_alias,
        uint8_t is_pinned) noexcept
    {
        double score = normalized_query.empty() ? (is_pinned ? 1000.0 : 0.0) : base_score;
        if (!normalized_query.empty() && !normalized_alias.empty() &&
            vnotch::starts_with_ordinal(normalized_alias, normalized_query))
        {
            double alias_boost = (normalized_alias == normalized_query) ? 2000.0 : 1100.0;
            score = std::max(score, alias_boost);
        }
        return (score > 0.0 && is_pinned != 0) ? (score + 150.0) : score;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_spotlight_prefs_has_valid_folder_syntax(
    const char16_t* folder_ptr,
    int32_t folder_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = (folder_ptr != nullptr &&
                  vnotch::services::SpotlightPreferences::HasValidFolderSyntax(
                      vnotch::make_u16_view(folder_ptr, folder_len))) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_prefs_is_path_under_folder(
    const char16_t* path_ptr,
    int32_t path_len,
    const char16_t* folder_ptr,
    int32_t folder_len,
    uint8_t* out_is_under) noexcept
{
    if (out_is_under == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_under = vnotch::services::SpotlightPreferences::IsPathUnderFolder(
        vnotch::make_u16_view(path_ptr, path_len),
        vnotch::make_u16_view(folder_ptr, folder_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_prefs_compute_score(
    double base_score,
    const char16_t* query_ptr,
    int32_t query_len,
    const char16_t* alias_ptr,
    int32_t alias_len,
    uint8_t is_pinned,
    double* out_score) noexcept
{
    if (out_score == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_score = vnotch::services::SpotlightPreferences::ComputePreferenceScore(
        base_score,
        vnotch::make_u16_view(query_ptr, query_len),
        vnotch::make_u16_view(alias_ptr, alias_len),
        is_pinned);
    return VN_OK;
}

} // extern "C"
