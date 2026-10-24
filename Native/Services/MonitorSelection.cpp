#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shellscalingapi.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <string>
#include <string_view>

#pragma comment(lib, "shcore.lib")

namespace vnotch::services {

class MonitorSelection final {
public:
    static double GetScale(int32_t center_x, int32_t center_y) noexcept {
        POINT pt{center_x, center_y};
        HMONITOR monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        UINT dpi_x = 0, dpi_y = 0;
        if (monitor != nullptr && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpi_x, &dpi_y) == S_OK && dpi_x > 0) {
            return static_cast<double>(dpi_x) / 96.0;
        }
        return 1.0;
    }

    static std::u16string GetDeviceId(std::u16string_view device_name) {
        std::wstring dev_w(reinterpret_cast<const wchar_t*>(device_name.data()), device_name.size());
        DISPLAY_DEVICEW dd{};
        dd.cb = sizeof(DISPLAY_DEVICEW);
        if (EnumDisplayDevicesW(dev_w.c_str(), 0, &dd, EDD_GET_DEVICE_INTERFACE_NAME) && dd.DeviceID[0] != L'\0') {
            size_t len = std::wcslen(dd.DeviceID);
            return std::u16string(reinterpret_cast<const char16_t*>(dd.DeviceID), len);
        }
        return std::u16string(device_name);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_monitor_get_scale(
    int32_t center_x, int32_t center_y, double* out_scale) noexcept
{
    if (!out_scale) return VN_ERR_INVALID_ARG;
    *out_scale = vnotch::services::MonitorSelection::GetScale(center_x, center_y);
    return VN_OK;
}

VN_EXPORT int32_t vn_monitor_get_device_id(
    const char16_t* dev_name_ptr, int32_t dev_name_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (!out_len) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    try {
        std::u16string_view dev_v = (dev_name_ptr && dev_name_len > 0)
            ? std::u16string_view(dev_name_ptr, static_cast<size_t>(dev_name_len))
            : std::u16string_view{};
        std::u16string res = vnotch::services::MonitorSelection::GetDeviceId(dev_v);
        int32_t req = static_cast<int32_t>(res.size());
        *out_len = req;
        if (req == 0) return VN_OK;
        if (!out_buf || out_cap < req) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_buf, res.data(), static_cast<size_t>(req) * sizeof(char16_t));
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
