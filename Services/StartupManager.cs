using System.IO;
using Microsoft.Win32;

namespace VNotch.Services;

public static class StartupManager
{
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "V-Notch";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch (Exception)
        {
            // Registry key might not exist or user lacks read permissions; treat as disabled.
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ??
                    Path.Combine(AppContext.BaseDirectory, "V-Notch.exe");
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
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
