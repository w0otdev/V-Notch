#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dwmapi.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <string_view>

#pragma comment(lib, "dwmapi.lib")

namespace vnotch::services {

#pragma pack(push, 1)
struct VnRect {
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
};
#pragma pack(pop)

class FullscreenDetector final {
public:
    static int32_t DetectFullscreenType(HWND hwnd, HWND notch_hwnd, HMONITOR notch_monitor) noexcept {
        RECT window_rect{};
        if (!IsCandidateWindow(hwnd, notch_hwnd, &window_rect)) {
            return 0; // None
        }

        MONITORINFO monitor_info{};
        if (!TryGetTargetMonitorInfo(hwnd, notch_monitor, &monitor_info)) {
            return 0; // None
        }

        return ClassifyFullscreenStyle(hwnd, window_rect, monitor_info);
    }

    static HMONITOR GetWindowMonitor(HWND hwnd) noexcept {
        return hwnd == nullptr ? nullptr : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    }

    static bool IsBlockedClass(HWND hwnd) noexcept {
        wchar_t buffer[160]{};
        int length = GetClassNameW(hwnd, buffer, 160);
        if (length <= 0) return false;
        return IsBlockedClassName(std::u16string_view(reinterpret_cast<const char16_t*>(buffer), static_cast<size_t>(length)));
    }

    static bool IsBlockedClassName(std::u16string_view class_name) noexcept {
        static constexpr std::array<std::u16string_view, 16> kExact = {
            u"Progman",
            u"WorkerW",
            u"Shell_TrayWnd",
            u"Shell_SecondaryTrayWnd",
            u"NotifyIconOverflowWindow",
            u"TaskListThumbnailWnd",
            u"MultitaskingViewFrame",
            u"ForegroundStaging",
            u"XamlExplorerHostIslandWindow",
            u"Windows.UI.Input.InputSite.WindowClass",
            u"Search_app",
            u"Tooltips_Class32",
            u"TaskSwitcherWnd",
            u"TaskSwitcherOverlayWnd",
            u"Windows.Internal.Shell.TabProxyWindow",
            u"Microsoft.UI.Content.DesktopChildSiteBridge"
        };

        for (const auto& exact : kExact) {
            if (class_name == exact) return true;
        }

        static constexpr std::array<std::u16string_view, 4> kPrefixes = {
            u"Windows.UI.Core",
            u"ImmersiveLauncher",
            u"TaskListOverlay",
            u"MSCTFIME"
        };

        for (const auto& prefix : kPrefixes) {
            if (class_name.starts_with(prefix)) return true;
        }

        return false;
    }

    static bool TryGetWindowBounds(HWND hwnd, RECT* out_rect) noexcept {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out_rect, sizeof(RECT)) == S_OK) {
            return true;
        }
        return GetWindowRect(hwnd, out_rect) != FALSE;
    }

    static bool IsWindowCloaked(HWND hwnd) noexcept {
        int cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == S_OK && cloaked != 0;
    }

private:
    static bool IsCandidateWindow(HWND hwnd, HWND notch_hwnd, RECT* out_window_rect) noexcept {
        *out_window_rect = RECT{};
        if (hwnd == nullptr || hwnd == notch_hwnd) {
            return false;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd)) {
            return false;
        }

        if (!TryGetWindowBounds(hwnd, out_window_rect)) {
            return false;
        }

        int32_t width = out_window_rect->right - out_window_rect->left;
        int32_t height = out_window_rect->bottom - out_window_rect->top;
        if (width < 480 || height < 320) {
            return false;
        }

        return !IsBlockedClass(hwnd);
    }

    static bool TryGetTargetMonitorInfo(HWND hwnd, HMONITOR notch_monitor, MONITORINFO* out_info) noexcept {
        out_info->cbSize = sizeof(MONITORINFO);
        HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == nullptr) {
            return false;
        }
        if (notch_monitor != nullptr && monitor != notch_monitor) {
            return false;
        }
        return GetMonitorInfoW(monitor, out_info) != FALSE;
    }

    static int32_t ClassifyFullscreenStyle(HWND hwnd, const RECT& window_rect, const MONITORINFO& monitor_info) noexcept {
        constexpr int32_t kFullscreenTolerancePx = 6;

        WINDOWPLACEMENT placement{};
        placement.length = sizeof(WINDOWPLACEMENT);
        bool is_maximized = GetWindowPlacement(hwnd, &placement) != FALSE && placement.showCmd == SW_SHOWMAXIMIZED;

        LONG style = GetWindowLongW(hwnd, GWL_STYLE);
        bool has_caption = (style & WS_CAPTION) == WS_CAPTION;
        bool has_resize_frame = (style & WS_THICKFRAME) != 0;

        if (RectCoversArea(window_rect, monitor_info.rcMonitor, kFullscreenTolerancePx)) {
            if (has_caption || has_resize_frame) {
                if (is_maximized) {
                    return 0; // None
                }
                return 2; // WindowedFullscreen
            }
            return 1; // ExclusiveFullscreen
        }

        constexpr int32_t kWorkAreaTolerancePx = 8;
        bool matches_work_area = RectMatchesArea(window_rect, monitor_info.rcWork, kWorkAreaTolerancePx);
        bool is_borderless = !has_caption && !has_resize_frame;
        bool is_windowed_fullscreen = matches_work_area && (is_borderless || (is_maximized && !has_caption));

        return is_windowed_fullscreen ? 2 : 0;
    }

    static bool RectCoversArea(const RECT& rect, const RECT& area, int32_t tolerance_px) noexcept {
        return rect.left <= area.left + tolerance_px &&
               rect.top <= area.top + tolerance_px &&
               rect.right >= area.right - tolerance_px &&
               rect.bottom >= area.bottom - tolerance_px;
    }

    static bool RectMatchesArea(const RECT& rect, const RECT& area, int32_t tolerance_px) noexcept {
        return std::abs(rect.left - area.left) <= tolerance_px &&
               std::abs(rect.top - area.top) <= tolerance_px &&
               std::abs(rect.right - area.right) <= tolerance_px &&
               std::abs(rect.bottom - area.bottom) <= tolerance_px;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_fullscreen_detect_type(
    intptr_t hwnd, intptr_t notch_hwnd, intptr_t notch_monitor, int32_t* out_type) noexcept
{
    if (!out_type) return VN_ERR_INVALID_ARG;
    *out_type = vnotch::services::FullscreenDetector::DetectFullscreenType(
        reinterpret_cast<HWND>(hwnd),
        reinterpret_cast<HWND>(notch_hwnd),
        reinterpret_cast<HMONITOR>(notch_monitor));
    return VN_OK;
}

VN_EXPORT int32_t vn_fullscreen_get_window_monitor(
    intptr_t hwnd, intptr_t* out_monitor) noexcept
{
    if (!out_monitor) return VN_ERR_INVALID_ARG;
    *out_monitor = reinterpret_cast<intptr_t>(
        vnotch::services::FullscreenDetector::GetWindowMonitor(reinterpret_cast<HWND>(hwnd)));
    return VN_OK;
}

VN_EXPORT int32_t vn_fullscreen_is_blocked_class(
    intptr_t hwnd, uint8_t* out_blocked) noexcept
{
    if (!out_blocked) return VN_ERR_INVALID_ARG;
    *out_blocked = vnotch::services::FullscreenDetector::IsBlockedClass(reinterpret_cast<HWND>(hwnd)) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_fullscreen_is_blocked_class_name(
    const char16_t* name_ptr, int32_t name_len, uint8_t* out_blocked) noexcept
{
    if (!out_blocked) return VN_ERR_INVALID_ARG;
    if (!name_ptr || name_len <= 0) {
        *out_blocked = 0;
        return VN_OK;
    }
    *out_blocked = vnotch::services::FullscreenDetector::IsBlockedClassName(
        std::u16string_view(name_ptr, static_cast<size_t>(name_len))) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_fullscreen_try_get_window_bounds(
    intptr_t hwnd, vnotch::services::VnRect* out_rect, uint8_t* out_ok) noexcept
{
    if (!out_rect || !out_ok) return VN_ERR_INVALID_ARG;
    RECT r{};
    bool ok = vnotch::services::FullscreenDetector::TryGetWindowBounds(reinterpret_cast<HWND>(hwnd), &r);
    out_rect->left = r.left;
    out_rect->top = r.top;
    out_rect->right = r.right;
    out_rect->bottom = r.bottom;
    *out_ok = ok ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_fullscreen_is_window_cloaked(
    intptr_t hwnd, uint8_t* out_cloaked) noexcept
{
    if (!out_cloaked) return VN_ERR_INVALID_ARG;
    *out_cloaked = vnotch::services::FullscreenDetector::IsWindowCloaked(reinterpret_cast<HWND>(hwnd)) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
