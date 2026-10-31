#include "vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services
{

class SpotlightUsageStore final
{
public:
    static constexpr double kCountBoostCap = 90.0;
    static constexpr double kCountBoostFactor = 22.0;

    static double ComputeBoost(
        int32_t count,
        int64_t last_launched_utc_ticks,
        int64_t now_utc_ticks,
        uint8_t remembered) noexcept
    {
        const int32_t safe_count = std::max(0, count);
        const double count_boost = std::min(
            kCountBoostCap,
            kCountBoostFactor * std::log2(1.0 + static_cast<double>(safe_count)));

        double recency_boost = 0.0;
        if (safe_count > 0)
        {
            double age_days = 0.0;
            if (now_utc_ticks > last_launched_utc_ticks)
            {
                const uint64_t elapsed_ticks =
                    static_cast<uint64_t>(now_utc_ticks) - static_cast<uint64_t>(last_launched_utc_ticks);
                age_days = static_cast<double>(elapsed_ticks) / static_cast<double>(vnotch::TICKS_PER_DAY);
            }

            if (age_days < 1.0) recency_boost = 35.0;
            else if (age_days < 3.0) recency_boost = 25.0;
            else if (age_days < 7.0) recency_boost = 15.0;
            else if (age_days < 30.0) recency_boost = 5.0;
        }

        return count_boost + recency_boost + (remembered != 0 ? 350.0 : 0.0);
    }

    static bool IsAllowedApplicationHistoryTarget(
        std::u16string_view target,
        std::u16string_view start_menu_root,
        std::u16string_view common_start_menu_root) noexcept
    {
        if (vnotch::starts_with_ignore_case(target, u"shell:AppsFolder\\"))
        {
            return true;
        }
        auto check_root = [&](std::u16string_view root) noexcept -> bool
        {
            if (root.empty() || target.size() <= root.size())
            {
                return false;
            }
            return vnotch::starts_with_ignore_case(target, root) &&
                   target[root.size()] == u'\\';
        };
        return check_root(start_menu_root) || check_root(common_start_menu_root);
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_spotlight_usage_compute_boost(
    int32_t count,
    int64_t last_launched_utc_ticks,
    int64_t now_utc_ticks,
    uint8_t remembered,
    double* out_boost) noexcept
{
    if (out_boost == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_boost = vnotch::services::SpotlightUsageStore::ComputeBoost(
        count, last_launched_utc_ticks, now_utc_ticks, remembered);
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_usage_is_allowed_app_history_target(
    const char16_t* target_ptr, int32_t target_len,
    const char16_t* start_menu_ptr, int32_t start_menu_len,
    const char16_t* common_start_menu_ptr, int32_t common_start_menu_len,
    uint8_t* out_allowed) noexcept
{
    if (out_allowed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_allowed = vnotch::services::SpotlightUsageStore::IsAllowedApplicationHistoryTarget(
        vnotch::make_u16_view(target_ptr, target_len),
        vnotch::make_u16_view(start_menu_ptr, start_menu_len),
        vnotch::make_u16_view(common_start_menu_ptr, common_start_menu_len)) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
