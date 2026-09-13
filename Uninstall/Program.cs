using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
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

        Application.EnableVisualStyles();
        InitializeLanguage();

        if (!silent)
        {
            var confirm = MessageBox.Show(
                Loc.Get("uninstall.confirmMessage"),
                Loc.Get("uninstall.title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
            {
                return 1;
            }
        }

        var installDirectory = ResolveInstallDirectory();
        var errors = new List<string>();

        TrySilently(StopRunningInstances, errors, Loc.Get("uninstall.action.stopApp"));
        TrySilently(RemoveStartupEntry, errors, Loc.Get("uninstall.action.removeStartup"));
        TrySilently(RemoveRegistryKeys, errors, Loc.Get("uninstall.action.removeRegistry"));
        TrySilently(RemoveShortcuts, errors, Loc.Get("uninstall.action.removeShortcuts"));
        TrySilently(RemoveAppData, errors, Loc.Get("uninstall.action.removeData"));

        // Hand final deletion to a temp script that waits for process exit,
        // deletes the install folder, and removes itself.
        TrySilently(() => ScheduleInstallDirRemoval(installDirectory), errors,
            Loc.Get("uninstall.action.removeInstallFolder"));

        if (!silent)
        {
            if (errors.Count == 0)
            {
                MessageBox.Show(
                    Loc.Get("uninstall.successMessage"),
                    Loc.Get("uninstall.successTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    Loc.Get("uninstall.warningMessage", string.Join("\n  - ", errors)),
                    Loc.Get("uninstall.warningTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
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
        catch
        {
        }

        Loc.SetLanguage(language);
    }

    private static string ResolveInstallDirectory()
    {
        var baseDir = AppContext.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(Path.Combine(baseDir, AppExeName)) ||
            File.Exists(Path.Combine(baseDir, "uninstall.exe")))
        {
            return baseDir;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppRegistryPath);
            if (key?.GetValue("InstallDir") is string fromReg && !string.IsNullOrWhiteSpace(fromReg))
            {
                return fromReg.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppName);
    }

    private static void StopRunningInstances()
    {
        var currentId = Environment.ProcessId;
        bool killedAny = false;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            try
            {
                if (process.Id == currentId)
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
            System.Threading.Thread.Sleep(1200);
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
        }
        catch (System.Security.SecurityException)
        {
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

        if (Directory.Exists(startMenuDirectory))
        {
            Directory.Delete(startMenuDirectory, recursive: true);
        }
    }

    private static void RemoveAppData()
    {
        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);
        DeleteDirectoryIfExists(appDataFolder);

        var localAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
        DeleteDirectoryIfExists(localAppDataFolder);

        // Leftover update installer in the temp folder.
        DeleteFileIfExists(Path.Combine(Path.GetTempPath(), "V-Notch-Setup.exe"));
    }

    private static void ScheduleInstallDirRemoval(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return;
        }

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"v-notch-uninstall-{Guid.NewGuid():N}.cmd");

        var script = string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            "setlocal",
            "rem Wait for the uninstaller to exit, then wipe the install folder.",
            "timeout /t 2 /nobreak >nul",
            $"rmdir /S /Q \"{installDirectory}\"",
            "rem Best-effort second pass in case files were still locked.",
            "timeout /t 2 /nobreak >nul",
            $"if exist \"{installDirectory}\" rmdir /S /Q \"{installDirectory}\"",
            $"del /Q \"{scriptPath}\""
        });

        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
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
