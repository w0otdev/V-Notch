using System.Runtime.InteropServices;

internal static class TranslationRuntimeSmoke
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);

    internal static void UseAppDllPolicy()
    {
        if (!SetDefaultDllDirectories(0x1000 | 0x800))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
