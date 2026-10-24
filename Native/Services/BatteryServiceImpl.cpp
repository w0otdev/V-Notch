#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class BatteryServiceImpl final
    {
    public:
        static int32_t vn_battery_apply_system_power_status_impl(const VnSystemPowerStatus* status,
    VnBatteryStateOut* io_state) noexcept
        {
    if (status == nullptr || io_state == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    bool has_battery = status->battery_flag != 255u && (status->battery_flag & 128u) == 0u;
    io_state->has_battery = has_battery ? 1u : 0u;
    io_state->percentage = (has_battery && status->battery_life_percent <= 100u)
        ? static_cast<int32_t>(status->battery_life_percent)
        : -1;
    io_state->is_plugged_in = status->ac_line_status == 1u ? 1u : 0u;
    io_state->is_charging = (has_battery && (status->battery_flag & 8u) != 0u) ? 1u : 0u;
    io_state->is_battery_saver = (status->system_status_flag & 1u) != 0u ? 1u : 0u;
    io_state->remaining_minutes = (has_battery && status->battery_life_time >= 0)
        ? (status->battery_life_time / 60)
        : -1;
    return VN_OK;
}

        static int32_t vn_battery_apply_nt_battery_state_impl(const VnSystemBatteryState* state,
    VnBatteryStateOut* io_state) noexcept
        {
    if (state == nullptr || io_state == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    bool present = state->battery_present != 0;
    io_state->has_battery = present ? 1u : 0u;
    io_state->is_plugged_in = state->ac_on_line != 0 ? 1u : 0u;
    io_state->is_charging = (present && state->charging != 0) ? 1u : 0u;
    if (!present)
    {
        io_state->percentage = -1;
        io_state->remaining_minutes = -1;
        io_state->power_watts = 0.0;
        io_state->has_power_rate = 0u;
        return VN_OK;
    }

    if (io_state->percentage < 0 &&
        state->max_capacity > 0u &&
        state->max_capacity != 0xFFFFFFFFu &&
        state->remaining_capacity != 0xFFFFFFFFu)
    {
        uint64_t pct = (static_cast<uint64_t>(state->remaining_capacity) * 100ULL) / static_cast<uint64_t>(state->max_capacity);
        io_state->percentage = static_cast<int32_t>(std::min<uint64_t>(100ULL, pct));
    }

    int64_t abs_rate = std::abs(static_cast<int64_t>(state->rate));
    bool has_rate = abs_rate < 200000LL;
    io_state->has_power_rate = has_rate ? 1u : 0u;
    io_state->power_watts = has_rate ? (static_cast<double>(state->rate) / 1000.0) : 0.0;
    return VN_OK;
}

        static int32_t vn_battery_query_all_impl(VnBatteryStateOut* out_state) noexcept
        {
    if (out_state == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_state = VnBatteryStateOut{ -1, 0, 0, 0, 0, -1, 0.0, 0 };

    SYSTEM_POWER_STATUS sps{};
    if (GetSystemPowerStatus(&sps))
    {
        VnSystemPowerStatus vsps{
            sps.ACLineStatus,
            sps.BatteryFlag,
            sps.BatteryLifePercent,
            sps.SystemStatusFlag,
            static_cast<int32_t>(sps.BatteryLifeTime),
            static_cast<int32_t>(sps.BatteryFullLifeTime)
        };
        vn_battery_apply_system_power_status_impl(&vsps, out_state);
    }

    VnSystemBatteryState nbs{};
    if (CallNtPowerInformation(SystemBatteryState, nullptr, 0, &nbs, sizeof(VnSystemBatteryState)) == 0)
    {
        vn_battery_apply_nt_battery_state_impl(&nbs, out_state);
    }
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_battery_apply_system_power_status(const VnSystemPowerStatus* status,
    VnBatteryStateOut* io_state) noexcept
{
    return vnotch::services::BatteryServiceImpl::vn_battery_apply_system_power_status_impl(status, io_state);
}

VN_EXPORT int32_t vn_battery_apply_nt_battery_state(const VnSystemBatteryState* state,
    VnBatteryStateOut* io_state) noexcept
{
    return vnotch::services::BatteryServiceImpl::vn_battery_apply_nt_battery_state_impl(state, io_state);
}

VN_EXPORT int32_t vn_battery_query_all(VnBatteryStateOut* out_state) noexcept
{
    return vnotch::services::BatteryServiceImpl::vn_battery_query_all_impl(out_state);
}

