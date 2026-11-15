#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
using namespace vnotch::ui;

VN_EXPORT int32_t __stdcall vn_mainwindow_step_volume_wheel(
    VnVolumeWheelState* io_state,
    int32_t raw_delta,
    int64_t now_utc_ticks,
    uint8_t is_indicator_active,
    int32_t* out_volume_delta,
    uint8_t* out_accepted,
    uint8_t* out_suppressed) noexcept
{
    if (!io_state || !out_volume_delta || !out_accepted || !out_suppressed)
        return VN_ERR_INVALID_ARG;

    *out_volume_delta = 0;
    *out_accepted = 0;
    *out_suppressed = 0;

    if (raw_delta == 0)
        return VN_OK;

    if (now_utc_ticks < io_state->suppress_until_utc_ticks)
    {
        io_state->accumulator = 0.0;
        io_state->last_wheel_utc_ticks = 0;
        io_state->is_wheel_active = 0;
        *out_suppressed = 1;
        return VN_OK;
    }

    constexpr int64_t kSessionResetTicks = 450LL * 10000LL;
    if (now_utc_ticks - io_state->last_wheel_utc_ticks > kSessionResetTicks)
    {
        io_state->accumulator = 0.0;
        io_state->is_wheel_active = 0;
    }
    io_state->last_wheel_utc_ticks = now_utc_ticks;

    if (is_indicator_active != 0 || io_state->is_wheel_active != 0)
    {
        io_state->is_wheel_active = 1;
        *out_volume_delta = raw_delta;
        *out_accepted = 1;
        return VN_OK;
    }

    constexpr int64_t kImmediateWheelDelta = 120;
    const int64_t abs_raw = raw_delta < 0 ? -static_cast<int64_t>(raw_delta) : static_cast<int64_t>(raw_delta);
    if (abs_raw >= kImmediateWheelDelta)
    {
        io_state->is_wheel_active = 1;
        *out_volume_delta = raw_delta;
        *out_accepted = 1;
        return VN_OK;
    }

    const int32_t acc_sign = io_state->accumulator > 0.0 ? 1 : (io_state->accumulator < 0.0 ? -1 : 0);
    const int32_t delta_sign = raw_delta > 0 ? 1 : -1;
    if (acc_sign != 0 && acc_sign != delta_sign)
        io_state->accumulator = 0.0;

    io_state->accumulator += static_cast<double>(raw_delta);
    if (std::abs(io_state->accumulator) < 240.0)
        return VN_OK;

    io_state->accumulator = 0.0;
    io_state->is_wheel_active = 1;
    *out_volume_delta = delta_sign * static_cast<int32_t>(kImmediateWheelDelta);
    *out_accepted = 1;
    return VN_OK;
}

