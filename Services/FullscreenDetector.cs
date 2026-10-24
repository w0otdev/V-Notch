using System;
using System.Runtime.InteropServices;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

internal enum FullscreenType
{
    None,
    ExclusiveFullscreen,
    WindowedFullscreen
}

internal static partial class FullscreenDetector
{
    public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd, IntPtr.Zero);
    }

    public static FullscreenType DetectFullscreenType(IntPtr hwnd, IntPtr notchHwnd, IntPtr notchMonitor)
    {
        vn_fullscreen_detect_type(hwnd, notchHwnd, notchMonitor, out int type);
        return (FullscreenType)type;
    }

    public static bool IsForegroundWindowFullscreen(IntPtr hwnd, IntPtr notchHwnd)
    {
        return DetectFullscreenType(hwnd, notchHwnd) != FullscreenType.None;
    }

    public static IntPtr GetWindowMonitor(IntPtr hwnd)
    {
        vn_fullscreen_get_window_monitor(hwnd, out IntPtr monitor);
        return monitor;
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

        return ProcessNameResolver.TryGetName(processId) ?? string.Empty;
    }

    internal static bool IsBlockedClass(IntPtr hwnd)
    {
        vn_fullscreen_is_blocked_class(hwnd, out byte blocked);
        return blocked != 0;
    }

    internal static unsafe bool IsBlockedClassName(ReadOnlySpan<char> className)
    {
        if (className.IsEmpty) return false;
        fixed (char* ptr = className)
        {
            vn_fullscreen_is_blocked_class_name(ptr, className.Length, out byte blocked);
            return blocked != 0;
        }
    }

    internal static bool TryGetWindowBounds(IntPtr hwnd, out RECT rect)
    {
        vn_fullscreen_try_get_window_bounds(hwnd, out VnRect nRect, out byte ok);
        rect = new RECT
        {
            Left = nRect.Left,
            Top = nRect.Top,
            Right = nRect.Right,
            Bottom = nRect.Bottom
        };
        return ok != 0;
    }

    internal static bool IsWindowCloaked(IntPtr hwnd)
    {
        vn_fullscreen_is_window_cloaked(hwnd, out byte cloaked);
        return cloaked != 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fullscreen_detect_type(
        IntPtr hwnd, IntPtr notch_hwnd, IntPtr notch_monitor, out int out_type);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fullscreen_get_window_monitor(
        IntPtr hwnd, out IntPtr out_monitor);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fullscreen_is_blocked_class(
        IntPtr hwnd, out byte out_blocked);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fullscreen_is_blocked_class_name(
        char* name_ptr, int name_len, out byte out_blocked);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fullscreen_try_get_window_bounds(
        IntPtr hwnd, out VnRect out_rect, out byte out_ok);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fullscreen_is_window_cloaked(
        IntPtr hwnd, out byte out_cloaked);
}
