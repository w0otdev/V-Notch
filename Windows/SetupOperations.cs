using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using VNotch.Services;

namespace VNotch;

internal sealed record SetupInstallOptions(
    string SourceDirectory,
    string InstallDirectory,
    bool StartWithWindows,
    string Language = "en");

internal sealed record SetupProgressInfo(
    string StatusText,
    int CurrentStep = 0,
    int TotalSteps = 0,
    bool IsIndeterminate = false);

internal static class SetupOperations
{
    private const string AppName = "V-Notch";
    private const string AppExeName = "V-Notch.exe";
    private const string Publisher = "rainaku";
    private const string AppUrl = "https://github.com/rainaku/V-Notch";
    private const string Version = "1.8.1";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\V-Notch";

    public static string GetDefaultInstallDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppName);
    }

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RequiresAdministratorForInstallPath(string installPath)
    {
        var fullPath = Path.GetFullPath(installPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var driveRoot = Path.GetPathRoot(fullPath)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrEmpty(driveRoot) &&
            string.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        foreach (var protectedRoot in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(protectedRoot))
            {
                continue;
            }

            var normalizedRoot = Path.GetFullPath(protectedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task InstallAsync(SetupInstallOptions options, Action<SetupProgressInfo> reportProgress)
    {
        await Task.Run(() =>
        {
            var sourceDirectory = Path.GetFullPath(options.SourceDirectory);
            var installDirectory = Path.GetFullPath(options.InstallDirectory);
            var sourceExePath = Path.Combine(sourceDirectory, AppExeName);
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException(Loc.Get("setup.install.sourceFolderMissing", sourceDirectory));
            }

            if (!File.Exists(sourceExePath))
            {
                throw new FileNotFoundException(Loc.Get("setup.install.sourceExeMissing", AppExeName), sourceExePath);
            }

            reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.closingRunning"), IsIndeterminate: true));
            StopOtherRunningInstances();

            reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.preparingFolder"), IsIndeterminate: true));
            Directory.CreateDirectory(installDirectory);

            var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                throw new InvalidOperationException(Loc.Get("setup.install.payloadEmpty"));
            }

            for (int i = 0; i < files.Length; i++)
            {
                var sourceFile = files[i];
                var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                var destinationFile = Path.Combine(installDirectory, relativePath);
                var destinationFolder = Path.GetDirectoryName(destinationFile);

                if (!string.IsNullOrEmpty(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                CopyFileWithRetry(sourceFile, destinationFile);
                reportProgress(new SetupProgressInfo(
                    VNotch.Services.Loc.Get("setup.install.installingFile", relativePath),
                    i + 1,
                    files.Length));
            }

            var installedExePath = Path.Combine(installDirectory, AppExeName);
            reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.creatingShortcuts"), files.Length, files.Length));
            CreateShortcuts(installedExePath, installDirectory);

            reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.savingStartup"), files.Length, files.Length));
            ConfigureStartup(installedExePath, options.StartWithWindows);

            reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.registeringUninstall"), files.Length, files.Length));
            RegisterUninstall(installedExePath, installDirectory);

            reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.savingPreferences"), files.Length, files.Length));
            SaveInitialSettings(options.Language);
        });
    }

    public static void LaunchInstalledApp(string installDirectory)
    {
        var installedExePath = Path.Combine(installDirectory, AppExeName);
        if (!File.Exists(installedExePath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(installedExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = installDirectory
        });
    }

    public static void RunUninstallFlow()
    {
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installedExePath = Path.Combine(installDirectory, AppExeName);

        var confirmation = MessageBox.Show(
            Loc.Get("setup.uninstall.confirm"),
            Loc.Get("setup.uninstall.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            Application.Current.Shutdown(1);
            return;
        }

        if (RequiresAdministratorForInstallPath(installDirectory) && !IsRunningAsAdministrator())
        {
            MessageBox.Show(
                Loc.Get("setup.uninstall.adminRequired"),
                Loc.Get("setup.windowTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Application.Current.Shutdown(1);
            return;
        }

        RemoveStartupRegistration();
        RemoveUninstallRegistration();
        RemoveShortcuts();

        var cleanupScriptPath = Path.Combine(
            Path.GetTempPath(),
            $"v-notch-uninstall-{Guid.NewGuid():N}.cmd");

        var scriptContents = string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            "setlocal",
            "timeout /t 2 /nobreak >nul",
            $"rmdir /S /Q \"{installDirectory}\"",
            $"del /Q \"{cleanupScriptPath}\""
        });

        File.WriteAllText(cleanupScriptPath, scriptContents);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cleanupScriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath()
        });

        Application.Current.Shutdown(0);
    }

    private static void StopOtherRunningInstances()
    {
        var currentProcessId = Environment.ProcessId;
        bool killedAny = false;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            try
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(8000);
                killedAny = true;
            }
            catch
            {
            }
        }

        if (killedAny)
        {
            System.Threading.Thread.Sleep(1500);
        }
    }

    private static void CopyFileWithRetry(string sourceFile, string destinationFile, int maxRetries = 5)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                System.Threading.Thread.Sleep(800 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                System.Threading.Thread.Sleep(800 * (attempt + 1));
            }
        }
    }

    private static void ConfigureStartup(string installedExePath, bool startWithWindows)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (runKey == null)
        {
            throw new InvalidOperationException(Loc.Get("setup.install.startupRegistryUnavailable"));
        }

        if (startWithWindows)
        {
            runKey.SetValue(AppName, $"\"{installedExePath}\"");
        }
        else
        {
            runKey.DeleteValue(AppName, false);
        }
    }

    private static void SaveInitialSettings(string language)
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "V-Notch");
            Directory.CreateDirectory(appFolder);

            var settingsPath = Path.Combine(appFolder, "settings.json");
            Models.NotchSettings settings;

            if (File.Exists(settingsPath))
            {
                var existingJson = File.ReadAllText(settingsPath);
                settings = System.Text.Json.JsonSerializer.Deserialize<Models.NotchSettings>(existingJson) ?? new Models.NotchSettings();
            }
            else
            {
                settings = new Models.NotchSettings();
            }

            settings.Language = language;
            
            var json = System.Text.Json.JsonSerializer.Serialize(settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
        }
    }

    private static void RegisterUninstall(string installedExePath, string installDirectory)
    {
        using var uninstallKey = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath);

        if (uninstallKey == null)
        {
            throw new InvalidOperationException(Loc.Get("setup.install.uninstallRegistryUnavailable"));
        }

        uninstallKey.SetValue("DisplayName", AppName);
        uninstallKey.SetValue("DisplayVersion", Version);
        uninstallKey.SetValue("Publisher", Publisher);
        uninstallKey.SetValue("URLInfoAbout", AppUrl);
        uninstallKey.SetValue("DisplayIcon", installedExePath);
        uninstallKey.SetValue("InstallLocation", installDirectory);

        // Prefer the dedicated uninstaller (full clean wipe, incl. app data).
        // Fall back to the in-app flow if it wasn't shipped with this build.
        var uninstallerPath = Path.Combine(installDirectory, "uninstall.exe");
        if (File.Exists(uninstallerPath))
        {
            uninstallKey.SetValue("UninstallString", $"\"{uninstallerPath}\"");
            uninstallKey.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /S");
        }
        else
        {
            uninstallKey.SetValue("UninstallString", $"\"{installedExePath}\" --uninstall");
            uninstallKey.SetValue("QuietUninstallString", $"\"{installedExePath}\" --uninstall");
        }

        using var appKey = Registry.CurrentUser.CreateSubKey($@"Software\{AppName}");
        appKey?.SetValue("InstallDir", installDirectory);
    }

    private static void RemoveUninstallRegistration()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{AppName}", throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveStartupRegistration()
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        runKey?.DeleteValue(AppName, false);
    }

    private static void CreateShortcuts(string installedExePath, string installDirectory)
    {
        var desktopShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk");

        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);
        Directory.CreateDirectory(startMenuDirectory);

        var startMenuShortcutPath = Path.Combine(startMenuDirectory, $"{AppName}.lnk");

        CreateShortcut(desktopShortcutPath, installedExePath, installDirectory);
        CreateShortcut(startMenuShortcutPath, installedExePath, installDirectory);
    }

    private static void RemoveShortcuts()
    {
        var desktopShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk");
        if (File.Exists(desktopShortcutPath))
        {
            File.Delete(desktopShortcutPath);
        }

        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);
        var startMenuShortcutPath = Path.Combine(startMenuDirectory, $"{AppName}.lnk");
        if (File.Exists(startMenuShortcutPath))
        {
            File.Delete(startMenuShortcutPath);
        }

        if (Directory.Exists(startMenuDirectory))
        {
            Directory.Delete(startMenuDirectory, recursive: true);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            throw new InvalidOperationException("Windows Script Host is unavailable for creating shortcuts.");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = targetPath;
            shortcut.Description = AppName;
            shortcut.Save();
        }
        finally
        {
            if (shell is not null)
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
