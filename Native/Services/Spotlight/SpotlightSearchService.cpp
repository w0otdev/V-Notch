#include "vnotch_common.h"
#include <algorithm>

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

} // extern "C"
