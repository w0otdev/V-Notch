using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using VNotch.Services;

namespace VNotch.Uninstaller;

internal static class Program
{
    private const string AppName = "V-Notch";
    private const string AppExeName = "V-Notch.exe";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\V-Notch";
    private const string RunRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryPath = @"Software\V-Notch";

    // Old Inno Setup install (kept for users upgrading from legacy builds).
    private const string LegacyInnoUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2567AF4-8BAE-46D2-B83E-56539C82F888}_is1";

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = args.Any(a =>
            a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        InitializeLanguage();

        if (!silent)
        {
            bool confirmed = SetupDialogs.ConfirmUninstall(
                Loc.Get("uninstall.confirmMessage"),
                Loc.Get("uninstall.title"));

            if (!confirmed)
            {
                return 1;
            }
        }

        string? installDirectory = null;
        var errors = new List<string>();

        TrySilently(() =>
        {
            installDirectory = ResolveInstallDirectory();
            InstallDirectoryCleanup.CreateStartInfo(installDirectory, Environment.ProcessId);
        }, errors, Loc.Get("uninstall.action.removeInstallFolder"));
        if (errors.Count == 0) TrySilently(StopRunningInstances, errors, Loc.Get("uninstall.action.stopApp"));
        if (errors.Count > 0)
        {
            if (!silent) SetupDialogs.ShowUninstallResult(
                Loc.Get("uninstall.warningMessage", string.Join("\n  - ", errors)), Loc.Get("uninstall.warningTitle"), hasErrors: true);
            return 2;
        }
        TrySilently(RemoveStartupEntry, errors, Loc.Get("uninstall.action.removeStartup"));
        TrySilently(RemoveRegistryKeys, errors, Loc.Get("uninstall.action.removeRegistry"));
        TrySilently(RemoveShortcuts, errors, Loc.Get("uninstall.action.removeShortcuts"));
        TrySilently(() => RemoveAppData(installDirectory!), errors, Loc.Get("uninstall.action.removeData"));

        // Hand final deletion to a helper that waits for this process to exit.
        TrySilently(() => ScheduleInstallDirRemoval(installDirectory!), errors,
            Loc.Get("uninstall.action.removeInstallFolder"));

        if (!silent)
        {
            if (errors.Count == 0)
            {
                SetupDialogs.ShowUninstallResult(
                    Loc.Get("uninstall.successMessage"),
                    Loc.Get("uninstall.successTitle"),
                    hasErrors: false);
            }
            else
            {
                SetupDialogs.ShowUninstallResult(
                    Loc.Get("uninstall.warningMessage", string.Join("\n  - ", errors)),
                    Loc.Get("uninstall.warningTitle"),
                    hasErrors: true);
            }
        }

        return errors.Count == 0 ? 0 : 2;
    }

    private static void InitializeLanguage()
    {
        string language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName,
                "settings.json");
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("Language", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    language = value.GetString()!;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings only lose the saved language; fall back to the OS culture.
            Debug.WriteLine($"[Uninstall] Could not read language from settings: {ex.Message}");
        }

        Loc.SetLanguage(language);
    }

    private static bool IsDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var root = Path.GetPathRoot(path);
        return string.Equals(root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInstallDirectory()
    {
        // A user-writable registry value must never select a recursive deletion target.
        return InstallDirectoryCleanup.ValidateDirectory(AppContext.BaseDirectory);
    }

    private const int ProcessExitTimeoutMs = 8000;

    private static void StopRunningInstances()
    {
        var currentId = Environment.ProcessId;
        var stillRunning = new List<int>();

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            using (process)
            {
                if (process.Id == currentId)
                {
                    continue;
                }

                try
                {
                    AppProcessShutdown.StopAsync(process).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and Kill.
                    continue;
                }
                catch (Win32Exception ex)
                {
                    // Access denied or the process is already terminating; the wait below decides.
                    Debug.WriteLine($"[Uninstall] Could not kill process {process.Id}: {ex.Message}");
                }
                catch (TimeoutException)
                {
                    stillRunning.Add(process.Id);
                    continue;
                }

                try
                {
                    // File handles can outlive the process briefly; the delete helpers retry for that.
                    if (!process.WaitForExit(ProcessExitTimeoutMs))
                    {
                        stillRunning.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    Debug.WriteLine($"[Uninstall] Could not wait for process {process.Id}: {ex.Message}");
                    stillRunning.Add(process.Id);
                }
            }
        }

        if (stillRunning.Count > 0)
        {
            throw new InvalidOperationException(
                $"{AppExeName} is still running (PID {string.Join(", ", stillRunning)}).");
        }
    }

    private static void RemoveStartupEntry()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
        runKey?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static void RemoveRegistryKeys()
    {
        DeleteSubKeyTree(Registry.CurrentUser, UninstallRegistryPath);
        DeleteSubKeyTree(Registry.LocalMachine, UninstallRegistryPath);
        DeleteSubKeyTree(Registry.CurrentUser, AppRegistryPath);
        DeleteSubKeyTree(Registry.CurrentUser, LegacyInnoUninstallKey);
        DeleteSubKeyTree(Registry.LocalMachine, LegacyInnoUninstallKey);
    }

    private static void DeleteSubKeyTree(RegistryKey root, string path)
    {
        try
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore HKLM keys when running without elevation.
        }
        catch (System.Security.SecurityException)
        {
            // Ignore HKLM keys when running without elevation.
        }
    }

    private static void RemoveShortcuts()
    {
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk");
        DeleteFileIfExists(desktopShortcut);

        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);

        SafeFileDeletion.DeleteShortcutDirectory(startMenuDirectory);
    }

    private static void RemoveAppData(string installDirectory)
    {
        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);
        if (!SafeFileDeletion.PathsOverlap(appDataFolder, installDirectory)) DeleteDirectoryIfExists(appDataFolder);

        var localAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
        if (!SafeFileDeletion.PathsOverlap(localAppDataFolder, installDirectory)) DeleteDirectoryIfExists(localAppDataFolder);

        // Builds before the app-data migration stored chats/models under this name.
        string legacyData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNotch");
        if (!SafeFileDeletion.PathsOverlap(legacyData, installDirectory)) DeleteDirectoryIfExists(legacyData);

        // Leftover update installer in the temp folder.
        DeleteFileIfExists(Path.Combine(Path.GetTempPath(), "V-Notch-Setup.exe"));
        // Temp/V-Notch can contain recovery copies and user files; never wipe the shared root.
    }

    private static void ScheduleInstallDirRemoval(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory) || IsDriveRoot(installDirectory))
        {
            return;
        }

        if (!File.Exists(Path.Combine(installDirectory, AppExeName)))
        {
            return;
        }

        InstallDirectoryCleanup.Schedule(installDirectory);
    }

    private const int DeleteAttempts = 5;
    private const int DeleteRetryDelayMs = 250;

    private static void DeleteFileIfExists(string path)
    {
        RetryDelete(() =>
        {
            if (File.Exists(path))
            {
                SafeFileDeletion.DeleteFile(path);
            }
        }, path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        RetryDelete(() =>
        {
            if (Directory.Exists(path))
            {
                SafeFileDeletion.DeleteDirectory(path);
            }
        }, path);
    }

    // A just-killed V-Notch can hold file handles for a moment after exit.
    private static void RetryDelete(Action delete, string path)
    {
        for (int attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (Exception ex) when (attempt < DeleteAttempts && ex is (IOException or UnauthorizedAccessException))
            {
                Debug.WriteLine($"[Uninstall] Delete attempt {attempt} failed for '{path}': {ex.Message}");
                System.Threading.Thread.Sleep(DeleteRetryDelayMs * attempt);
            }
        }
    }

    private static void TrySilently(Action action, List<string> errors, string description)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            errors.Add($"{description}: {ex.Message}");
        }
    }
}
