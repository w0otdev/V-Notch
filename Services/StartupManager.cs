using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed partial class WindowsStartupManager : IStartupManager
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_startup_is_enabled(out byte outEnabled);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_startup_set_enabled(
        byte enable, ReadOnlySpan<ushort> exePath, int exePathLen, out int outWin32Error);

    public bool IsAutoStartEnabled()
    {
        return vn_startup_is_enabled(out byte enabled) == 0 && enabled != 0;
    }

    public void SetAutoStart(bool enable)
    {
        string exePath = enable
            ? (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "V-Notch.exe"))
            : string.Empty;
        ReadOnlySpan<ushort> pathSpan = MemoryMarshal.Cast<char, ushort>(exePath.AsSpan());
        int status = vn_startup_set_enabled(enable ? (byte)1 : (byte)0, pathSpan, pathSpan.Length, out int win32Err);
        EnsureChangeSucceeded(status, win32Err);
    }

    internal static void EnsureChangeSucceeded(int status, int win32Error)
    {
        if (win32Error != 0) throw new Win32Exception(win32Error);
        if (status != 0) throw new InvalidOperationException($"Startup settings failed (native status {status}).");
    }
}
