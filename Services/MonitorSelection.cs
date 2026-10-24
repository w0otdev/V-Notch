using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VNotch.Models;

namespace VNotch.Services;

internal static partial class MonitorSelection
{
    internal sealed record Choice(string Id, string Name, int Index, Screen? Screen)
    {
        public override string ToString() => Name;
    }

    public static Choice[] GetChoices() => Screen.AllScreens.Select((screen, index) =>
        new Choice(GetId(screen), Loc.Get("settings.display.name", index + 1)
            + (screen.Primary ? Loc.Get("settings.display.primary") : "")
            + $" ({screen.Bounds.Width} × {screen.Bounds.Height})", index, screen)).ToArray();

    public static Screen Resolve(NotchSettings settings)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return Screen.PrimaryScreen!;

        if (string.IsNullOrEmpty(settings.MonitorDeviceId))
        {
            if ((uint)settings.MonitorIndex < (uint)screens.Length)
                return screens[settings.MonitorIndex];
        }
        else
        {
            foreach (var screen in screens)
            {
                if (string.Equals(GetId(screen), settings.MonitorDeviceId, StringComparison.OrdinalIgnoreCase))
                    return screen;
            }
        }

        return Screen.PrimaryScreen ?? screens[0];
    }

    public static double GetScale(Screen screen)
    {
        var bounds = screen.Bounds;
        vn_monitor_get_scale(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, out double scale);
        return scale;
    }

    private static string GetId(Screen screen)
    {
        string devName = screen.DeviceName ?? string.Empty;
        Span<char> buf = stackalloc char[256];
        unsafe
        {
            fixed (char* pDev = devName)
            fixed (char* pBuf = buf)
            {
                int rc = vn_monitor_get_device_id(pDev, devName.Length, pBuf, buf.Length, out int outLen);
                if (rc == 0 && outLen > 0)
                    return new string(buf[..outLen]);
            }
        }
        return devName;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_monitor_get_scale(
        int center_x, int center_y, out double out_scale);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_monitor_get_device_id(
        char* dev_name_ptr, int dev_name_len,
        char* out_buf, int out_cap, out int out_len);
}
