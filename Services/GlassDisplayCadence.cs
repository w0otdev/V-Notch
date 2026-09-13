using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static class GlassDisplayCadence
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Win32Interop.RECT Monitor;
        public Win32Interop.RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx info);

    internal static int? GetRefreshRate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        IntPtr monitor = Win32Interop.MonitorFromWindow(hwnd, 2); // nearest display
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
        if (monitor == IntPtr.Zero || !GetMonitorInfoEx(monitor, ref info)) return null;
        var mode = new Win32Interop.DEVMODE
        {
            dmSize = (ushort)Marshal.SizeOf<Win32Interop.DEVMODE>()
        };
        if (!Win32Interop.EnumDisplaySettings(info.Device, Win32Interop.ENUM_CURRENT_SETTINGS, ref mode))
            return null;
        return mode.dmDisplayFrequency is >= 24 and <= 1000
            ? (int)mode.dmDisplayFrequency : null;
    }
}
