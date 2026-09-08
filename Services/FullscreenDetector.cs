using System;
using System.Runtime.InteropServices;
using System.Text;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

internal enum FullscreenType
{
    None,
    ExclusiveFullscreen,
    WindowedFullscreen
}

internal static class FullscreenDetector
{
    private static readonly string[] BlockedClassNamesExact = new[]
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "Windows.UI.Input.InputSite.WindowClass",
        "Search_app",
        "Tooltips_Class32",
        "TaskSwitcherWnd",
        "TaskSwitcherOverlayWnd",
        "Windows.Internal.Shell.TabProxyWindow",
        "Microsoft.UI.Content.DesktopChildSiteBridge"
    };

    private static readonly string[] BlockedClassNamePrefixes = new[]
    {
        "Windows.UI.Core",
        "ImmersiveLauncher",
        "TaskListOverlay",
        "MSCTFIME"
    };

    public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd, IntPtr.Zero);
    }

    public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd, IntPtr notchMonitor)
    {
        if (!IsCandidateWindow(hwnd, notchHwnd, out var windowRect))
        {
            return FullscreenType.None;
        }

        if (!TryGetTargetMonitorInfo(hwnd, notchMonitor, out var monitorInfo))
        {
            return FullscreenType.None;
        }

        return ClassifyFullscreenStyle(hwnd, windowRect, monitorInfo);
    }

    private static bool IsCandidateWindow(IntPtr hwnd, IntPtr notchHwnd, out RECT windowRect)
    {
        windowRect = default;
        if (hwnd == IntPtr.Zero || hwnd == notchHwnd)
        {
            return false;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
        {
            return false;
        }

        if (!TryGetWindowBounds(hwnd, out windowRect))
        {
            return false;
        }

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        if (width < 480 || height < 320)
        {
            return false;
        }

        return !IsBlockedClass(hwnd);
    }

    private static bool TryGetTargetMonitorInfo(IntPtr hwnd, IntPtr notchMonitor, out MONITORINFO monitorInfo)
    {
        monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        if (notchMonitor != IntPtr.Zero && monitor != notchMonitor)
        {
            return false;
        }

        return GetMonitorInfo(monitor, ref monitorInfo);
    }

    private static FullscreenType ClassifyFullscreenStyle(IntPtr hwnd, RECT windowRect, in MONITORINFO monitorInfo)
    {
        const int fullscreenTolerancePx = 6;

        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        bool isMaximized = GetWindowPlacement(hwnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;

        int style = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasResizeFrame = (style & WS_THICKFRAME) != 0;

        if (RectCoversArea(windowRect, monitorInfo.rcMonitor, fullscreenTolerancePx))
        {
            if (hasCaption || hasResizeFrame)
            {
                if (isMaximized)
                {
                    return FullscreenType.None;
                }

                return FullscreenType.WindowedFullscreen;
            }
            return FullscreenType.ExclusiveFullscreen;
        }

        const int workAreaTolerancePx = 8;
        bool matchesWorkArea = RectMatchesArea(windowRect, monitorInfo.rcWork, workAreaTolerancePx);
        bool isBorderless = !hasCaption && !hasResizeFrame;
        bool isWindowedFullscreen = matchesWorkArea && (isBorderless || (isMaximized && !hasCaption));

        return isWindowedFullscreen ? FullscreenType.WindowedFullscreen : FullscreenType.None;
    }

    public static bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd) != FullscreenType.None;
    }

    public static IntPtr GetWindowMonitor(IntPtr hwnd)
    {
        return hwnd == IntPtr.Zero
            ? IntPtr.Zero
            : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    }

    public static string TryGetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static bool IsBlockedClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(160);
        if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return false;
        string className = sb.ToString();

        for (int i = 0; i < BlockedClassNamesExact.Length; i++)
        {
            if (string.Equals(className, BlockedClassNamesExact[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < BlockedClassNamePrefixes.Length; i++)
        {
            if (className.StartsWith(BlockedClassNamePrefixes[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetWindowBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttributeRect(
                hwnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect,
                Marshal.SizeOf<RECT>()) == 0)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    internal static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttributeInt(
                   hwnd,
                   DWMWA_CLOAKED,
                   out int cloaked,
                   sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool RectCoversArea(RECT rect, RECT area, int tolerancePx)
    {
        return rect.Left <= area.Left + tolerancePx &&
               rect.Top <= area.Top + tolerancePx &&
               rect.Right >= area.Right - tolerancePx &&
               rect.Bottom >= area.Bottom - tolerancePx;
    }

    private static bool RectMatchesArea(RECT rect, RECT area, int tolerancePx)
    {
        return Math.Abs(rect.Left - area.Left) <= tolerancePx &&
               Math.Abs(rect.Top - area.Top) <= tolerancePx &&
               Math.Abs(rect.Right - area.Right) <= tolerancePx &&
               Math.Abs(rect.Bottom - area.Bottom) <= tolerancePx;
    }
}
