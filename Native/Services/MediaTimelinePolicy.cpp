#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class MediaTimelinePolicy final
    {
    public:
        static constexpr bool has_finite_duration_ticks(int64_t duration_ticks) noexcept
        {
            return duration_ticks > 0 && duration_ticks <= (30LL * TICKS_PER_DAY);
        }
        
        static constexpr bool is_indeterminate_ticks(int64_t duration_ticks, bool reported_indeterminate) noexcept
        {
            return reported_indeterminate || !has_finite_duration_ticks(duration_ticks);
        }
        
        static constexpr int64_t duration_compensation_window_ticks(int64_t duration_ticks) noexcept
        {
            int64_t window = duration_ticks > 0
                ? (duration_ticks + 5LL * TICKS_PER_SECOND)
                : (10LL * TICKS_PER_MINUTE);
            int64_t four_hours = 4LL * TICKS_PER_HOUR;
            return window < four_hours ? window : four_hours;
        }

        static int32_t vn_timeline_policy_has_finite_duration_impl(int64_t duration_ticks, uint8_t* out_finite) noexcept
        {
    if (out_finite == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_finite = has_finite_duration_ticks(duration_ticks) ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_timeline_policy_is_indeterminate_impl(int64_t duration_ticks, uint8_t reported_indeterminate, uint8_t* out_indeterminate) noexcept
        {
    if (out_indeterminate == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_indeterminate = is_indeterminate_ticks(duration_ticks, reported_indeterminate != 0) ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_timeline_policy_has_finite_duration(int64_t duration_ticks, uint8_t* out_finite) noexcept
{
    return vnotch::services::MediaTimelinePolicy::vn_timeline_policy_has_finite_duration_impl(duration_ticks, out_finite);
}

VN_EXPORT int32_t vn_timeline_policy_is_indeterminate(int64_t duration_ticks, uint8_t reported_indeterminate, uint8_t* out_indeterminate) noexcept
{
    return vnotch::services::MediaTimelinePolicy::vn_timeline_policy_is_indeterminate_impl(duration_ticks, reported_indeterminate, out_indeterminate);
}

