using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class GlassDisplayCadence
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_glass_display_cadence_get_refresh_rate(
        IntPtr hwnd,
        out byte outHasValue,
        out int outHz);

    internal static int? GetRefreshRate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (vn_glass_display_cadence_get_refresh_rate(hwnd, out byte hasVal, out int hz) == 0 && hasVal != 0)
            return hz;
        return null;
    }
}
