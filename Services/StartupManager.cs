using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class StartupManager
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_startup_is_enabled(out byte outEnabled);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_startup_set_enabled(
        byte enable, ReadOnlySpan<ushort> exePath, int exePathLen, out int outWin32Error);

    public static bool IsAutoStartEnabled()
    {
        return vn_startup_is_enabled(out byte enabled) == 0 && enabled != 0;
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            string exePath = enable
                ? (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "V-Notch.exe"))
                : string.Empty;
            ReadOnlySpan<ushort> pathSpan = MemoryMarshal.Cast<char, ushort>(exePath.AsSpan());
            if (vn_startup_set_enabled(enable ? (byte)1 : (byte)0, pathSpan, pathSpan.Length, out int win32Err) == 0 &&
                win32Err != 0 && win32Err != 2)
            {
                throw new Win32Exception(win32Err);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Loc.Get("error.startupChange", ex.Message),
                Loc.Get("error.title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}

public sealed class WindowsStartupManager : IStartupManager
{
    public bool IsAutoStartEnabled() => StartupManager.IsAutoStartEnabled();
    public void SetAutoStart(bool enable) => StartupManager.SetAutoStart(enable);
}
