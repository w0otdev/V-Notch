using System.Runtime.InteropServices;
using System.Windows.Forms;
using VNotch.Models;

namespace VNotch.Services;

internal static class MonitorSelection
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
        var choices = GetChoices();
        var selected = string.IsNullOrEmpty(settings.MonitorDeviceId)
            ? choices.FirstOrDefault(c => c.Index == settings.MonitorIndex)
            : choices.FirstOrDefault(c => string.Equals(c.Id, settings.MonitorDeviceId, StringComparison.OrdinalIgnoreCase));
        return selected?.Screen ?? Screen.PrimaryScreen ?? choices[0].Screen!;
    }

    public static double GetScale(Screen screen)
    {
        var bounds = screen.Bounds;
        var monitor = Win32Interop.MonitorFromPoint(new Win32Interop.POINT
            { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 }, 2);
        return monitor != IntPtr.Zero && Win32Interop.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0 : 1.0;
    }

    private static string GetId(Screen screen)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        // Monitor interface identity survives changes to the display enumeration order.
        return EnumDisplayDevices(screen.DeviceName, 0, ref device, 1) && !string.IsNullOrEmpty(device.Id)
            ? device.Id : screen.DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
}
