#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
using namespace vnotch::ui;
namespace {
struct DesktopObscureProbeCtx
{
    HWND notch_hwnd;
    DWORD my_pid;
    RECT notch_rect;
    uint8_t obscured;
};

bool IsWindowCloakedLocal(HWND hwnd) noexcept
{
    int cloaked = 0;
    return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == S_OK && cloaked != 0;
}

bool IsBlockedDesktopClassLocal(HWND hwnd) noexcept
{
    wchar_t buffer[160]{};
    const int len = GetClassNameW(hwnd, buffer, 160);
    if (len <= 0)
        return false;

    const std::u16string_view class_name(reinterpret_cast<const char16_t*>(buffer), static_cast<size_t>(len));

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

    for (const auto& exact : kExact)
    {
        if (class_name == exact)
            return true;
    }

    static constexpr std::array<std::u16string_view, 4> kPrefixes = {
        u"Windows.UI.Core",
        u"ImmersiveLauncher",
        u"TaskListOverlay",
        u"MSCTFIME"
    };

    for (const auto& prefix : kPrefixes)
    {
        if (class_name.starts_with(prefix))
            return true;
    }

    return false;
}

bool TryGetWindowBoundsLocal(HWND hwnd, RECT* out_rect) noexcept
{
    if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out_rect, sizeof(RECT)) == S_OK)
        return true;
    return GetWindowRect(hwnd, out_rect) != FALSE;
}

BOOL CALLBACK EnumObscuringWindowsProc(HWND hwnd, LPARAM lparam) noexcept
{
    auto* ctx = reinterpret_cast<DesktopObscureProbeCtx*>(lparam);
    if (hwnd == ctx->notch_hwnd)
        return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == ctx->my_pid)
        return TRUE;

    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloakedLocal(hwnd))
        return TRUE;

    if (IsBlockedDesktopClassLocal(hwnd))
        return TRUE;

    const LONG ex_style = GetWindowLongW(hwnd, GWL_EXSTYLE);
    if ((ex_style & WS_EX_TOOLWINDOW) != 0 && (ex_style & WS_EX_APPWINDOW) == 0)
        return TRUE;
    if ((ex_style & WS_EX_TRANSPARENT) != 0)
        return TRUE;

    RECT win_rect{};
    if (TryGetWindowBoundsLocal(hwnd, &win_rect) &&
        win_rect.left < ctx->notch_rect.right &&
        win_rect.right > ctx->notch_rect.left &&
        win_rect.top < ctx->notch_rect.bottom &&
        win_rect.bottom > ctx->notch_rect.top)
    {
        ctx->obscured = 1;
        return FALSE;
    }

    return TRUE;
}

}

VN_EXPORT int32_t __stdcall vn_desktop_edge_probe_notch_obscured(
    intptr_t self_hwnd,
    uint8_t* out_obscured) noexcept
{
    if (!out_obscured)
        return VN_ERR_INVALID_ARG;

    *out_obscured = 0;
    const HWND notch_hwnd = reinterpret_cast<HWND>(self_hwnd);
    if (notch_hwnd == nullptr)
        return VN_OK;

    RECT notch_rect{};
    if (!GetWindowRect(notch_hwnd, &notch_rect))
        return VN_OK;
    if (notch_rect.right <= notch_rect.left || notch_rect.bottom <= notch_rect.top)
        return VN_OK;

    DWORD my_pid = 0;
    GetWindowThreadProcessId(notch_hwnd, &my_pid);

    DesktopObscureProbeCtx ctx{ notch_hwnd, my_pid, notch_rect, 0 };
    EnumWindows(EnumObscuringWindowsProc, reinterpret_cast<LPARAM>(&ctx));
    *out_obscured = ctx.obscured;
    return VN_OK;
}

