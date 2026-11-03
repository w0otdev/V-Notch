#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include "vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services
{

class SpotlightSearchService final
{
public:
    static constexpr int32_t kMaxQueryLength = 256;
    static constexpr int32_t kMaxResults = 50;

    static bool TryNormalizeInput(
        std::u16string_view query,
        int32_t limit,
        int32_t& out_start,
        int32_t& out_len,
        int32_t& out_limit) noexcept
    {
        std::u16string_view trimmed = vnotch::trim_view(query);
        if (trimmed.empty())
        {
            out_start = 0;
            out_len = 0;
            out_limit = std::clamp(limit, 0, kMaxResults);
            return false;
        }
        out_start = static_cast<int32_t>(trimmed.data() - query.data());
        out_len = std::min(static_cast<int32_t>(trimmed.size()), kMaxQueryLength);
        out_limit = std::clamp(limit, 0, kMaxResults);
        return out_len > 0 && out_limit > 0;
    }

    static constexpr double ComputeBoostedScore(double base_score, double usage_boost, uint8_t is_pinned) noexcept
    {
        return base_score + usage_boost + (is_pinned != 0 ? 150.0 : 0.0);
    }

    static int32_t CompareKeys(
        double score_a,
        std::u16string_view title_a,
        std::u16string_view id_a,
        double score_b,
        std::u16string_view title_b,
        std::u16string_view id_b) noexcept
    {
        if (std::isnan(score_a) || std::isnan(score_b))
        {
            if (!std::isnan(score_a)) return -1;
            if (!std::isnan(score_b)) return 1;
        }
        else
        {
            if (score_b > score_a) return 1;
            if (score_b < score_a) return -1;
        }

        if (title_a.empty() || title_b.empty())
        {
            if (title_a.size() != title_b.size())
            {
                return title_a.empty() ? -1 : 1;
            }
        }
        else
        {
            int cmp = ::CompareStringEx(
                LOCALE_NAME_USER_DEFAULT,
                LINGUISTIC_IGNORECASE,
                reinterpret_cast<LPCWCH>(title_a.data()),
                static_cast<int>(title_a.size()),
                reinterpret_cast<LPCWCH>(title_b.data()),
                static_cast<int>(title_b.size()),
                nullptr,
                nullptr,
                0);
            if (cmp == CSTR_LESS_THAN) return -1;
            if (cmp == CSTR_GREATER_THAN) return 1;
        }

        int id_cmp = id_a.compare(id_b);
        if (id_cmp < 0) return -1;
        if (id_cmp > 0) return 1;
        return 0;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_spotlight_search_try_normalize_input(
    const char16_t* query_ptr,
    int32_t query_len,
    int32_t limit,
    uint8_t* out_valid,
    int32_t* out_start,
    int32_t* out_len,
    int32_t* out_limit) noexcept
{
    if (out_valid == nullptr || out_start == nullptr || out_len == nullptr || out_limit == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    int32_t s = 0, l = 0, lim = 0;
    bool ok = vnotch::services::SpotlightSearchService::TryNormalizeInput(
        vnotch::make_u16_view(query_ptr, query_len), limit, s, l, lim);
    *out_valid = ok ? 1u : 0u;
    *out_start = s;
    *out_len = l;
    *out_limit = lim;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_search_compute_boosted_score(
    double base_score,
    double usage_boost,
    uint8_t is_pinned,
    double* out_score) noexcept
{
    if (out_score == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_score = vnotch::services::SpotlightSearchService::ComputeBoostedScore(base_score, usage_boost, is_pinned);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_search_compare_keys(
    double score_a,
    const char16_t* title_a_ptr,
    int32_t title_a_len,
    const char16_t* id_a_ptr,
    int32_t id_a_len,
    double score_b,
    const char16_t* title_b_ptr,
    int32_t title_b_len,
    const char16_t* id_b_ptr,
    int32_t id_b_len,
    int32_t* out_cmp) noexcept
{
    if (out_cmp == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_cmp = vnotch::services::SpotlightSearchService::CompareKeys(
        score_a,
        vnotch::make_u16_view(title_a_ptr, title_a_len),
        vnotch::make_u16_view(id_a_ptr, id_a_len),
        score_b,
        vnotch::make_u16_view(title_b_ptr, title_b_len),
        vnotch::make_u16_view(id_b_ptr, id_b_len));
    return VN_OK;
}

} // extern "C"
