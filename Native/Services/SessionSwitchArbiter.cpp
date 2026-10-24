#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SessionSwitchArbiter final
    {
    public:
        static int32_t vn_session_switch_hold_seconds_impl(uint8_t current_is_premium, double* out_seconds) noexcept
        {
    if (out_seconds == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_seconds = current_is_premium != 0 ? 4.0 : 1.5;
    return VN_OK;
}

        static int32_t vn_session_switch_should_hold_impl(const VnSessionSwitchInputs* inputs, uint8_t* out_should_hold) noexcept
        {
    if (inputs == nullptr || out_should_hold == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    double hold_sec = inputs->current_is_premium != 0 ? 4.0 : 1.5;
    bool hold = (inputs->best_is_recent_latest_playback == 0) &&
                (inputs->best_is_os_current == 0) &&
                (inputs->best_has_fresh_timeline == 0) &&
                (inputs->current_still_playing != 0) &&
                (inputs->pending_elapsed_seconds < hold_sec);
    *out_should_hold = hold ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_session_switch_hold_seconds(uint8_t current_is_premium, double* out_seconds) noexcept
{
    return vnotch::services::SessionSwitchArbiter::vn_session_switch_hold_seconds_impl(current_is_premium, out_seconds);
}

VN_EXPORT int32_t vn_session_switch_should_hold(const VnSessionSwitchInputs* inputs, uint8_t* out_should_hold) noexcept
{
    return vnotch::services::SessionSwitchArbiter::vn_session_switch_should_hold_impl(inputs, out_should_hold);
}

