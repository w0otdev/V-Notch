#include "vnotch_common.h"

namespace vnotch::services
{

class GlassDisplayCadence final
{
public:
    static int32_t GetRefreshRate(HWND hwnd, uint8_t* out_has_value, int32_t* out_hz) noexcept
    {
        if (out_has_value == nullptr || out_hz == nullptr)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_has_value = 0u;
        *out_hz = 0;

        if (hwnd == nullptr)
        {
            return VN_OK;
        }

        HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == nullptr)
        {
            return VN_OK;
        }

        MONITORINFOEXW info{};
        info.cbSize = sizeof(MONITORINFOEXW);
        if (!GetMonitorInfoW(monitor, &info))
        {
            return VN_OK;
        }

        DEVMODEW mode{};
        mode.dmSize = sizeof(DEVMODEW);
        if (!EnumDisplaySettingsW(info.szDevice, ENUM_CURRENT_SETTINGS, &mode))
        {
            return VN_OK;
        }

        if (mode.dmDisplayFrequency >= 24 && mode.dmDisplayFrequency <= 1000)
        {
            *out_has_value = 1u;
            *out_hz = static_cast<int32_t>(mode.dmDisplayFrequency);
        }
        return VN_OK;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_glass_display_cadence_get_refresh_rate(
    void* hwnd,
    uint8_t* out_has_value,
    int32_t* out_hz) noexcept
{
    return vnotch::services::GlassDisplayCadence::GetRefreshRate(
        static_cast<HWND>(hwnd), out_has_value, out_hz);
}

} // extern "C"
