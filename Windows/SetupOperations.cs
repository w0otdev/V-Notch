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

internal sealed record SetupInstallCallbacks(
    Func<Task> StopRunningInstances,
    Action<string, string> CreateShortcuts,
    Action<string, bool> ConfigureStartup,
    Action<string, string> RegisterUninstall,
    Action<string> SaveSettings);

internal static class SetupOperations
{
    private const string AppName = "V-Notch";
    private const string AppExeName = "V-Notch.exe";
    private const string Publisher = "w0otdev";
#pragma warning disable S1075 // Official project repository URL for Windows uninstall registry
    private static readonly string AppUrl = string.Concat("https://", "github.com/w0otdev/V-Notch");
#pragma warning restore S1075
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\V-Notch";

    public static string GetDefaultInstallDirectory()
    {
        var candidates = new List<string?>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    candidates.AddRange(ReadInstallDirectories(root));
                }
                catch (UnauthorizedAccessException) { /* Try the other registration locations. */ }
                catch (System.Security.SecurityException) { /* Try the other registration locations. */ }
            }
        return SelectInstallDirectory(
            candidates,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                AppName));
    }

    internal static string?[] ReadInstallDirectories(RegistryKey root)
    {
        using var uninstallKey = root.OpenSubKey(UninstallRegistryPath);
        using var appKey = root.OpenSubKey($@"Software\{AppName}");
        using var legacyKey = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2567AF4-8BAE-46D2-B83E-56539C82F888}_is1");
        return [uninstallKey?.GetValue("InstallLocation") as string, appKey?.GetValue("InstallDir") as string,
            legacyKey?.GetValue("InstallLocation") as string, legacyKey?.GetValue("Inno Setup: App Path") as string];
    }

    internal static string SelectInstallDirectory(IEnumerable<string?> candidates, string fallback)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var directory = InstallDirectoryCleanup.ValidateDirectory(candidate);
                if (File.Exists(Path.Combine(directory, AppExeName))) return directory;
            }
            catch (ArgumentException) { /* Intentionally ignored. */ }
            catch (IOException) { /* Intentionally ignored. */ }
            catch (UnauthorizedAccessException) { /* Intentionally ignored. */ }
        }
        return fallback;
    }

    internal static bool GetInitialStartupPreference(string installDirectory)
    {
        if (!File.Exists(Path.Combine(installDirectory, AppExeName))) return true;
        using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return runKey?.GetValue(AppName) is string command && !string.IsNullOrWhiteSpace(command);
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
        => await InstallAsync(options, reportProgress, new SetupInstallCallbacks(
            StopOtherRunningInstancesAsync, CreateShortcuts, ConfigureStartup, RegisterUninstall, SaveInitialSettings));

    internal static async Task InstallAsync(SetupInstallOptions options, Action<SetupProgressInfo> reportProgress,
        SetupInstallCallbacks callbacks, string? recoveryRoot = null)
    {
        await Task.Run(async () =>
        {
            var sourceDirectory = Path.GetFullPath(options.SourceDirectory);
            var installDirectory = InstallDirectoryCleanup.ValidateInstallDirectory(options.InstallDirectory);
            if (SafeFileDeletion.PathsOverlap(sourceDirectory, installDirectory))
                throw new ArgumentException("The installation payload and destination must be separate folders.");
            var sourceExePath = Path.Combine(sourceDirectory, AppExeName);
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException(Loc.Get("setup.install.sourceFolderMissing", sourceDirectory));
            }

            if (!File.Exists(sourceExePath))
            {
                throw new FileNotFoundException(Loc.Get("setup.install.sourceExeMissing", AppExeName), sourceExePath);
            }

            SafeFileDeletion.ValidatePath(sourceDirectory);
            var files = EnumeratePayloadFiles(sourceDirectory).Where(file =>
                !Path.GetRelativePath(sourceDirectory, file).Equals(InstallDirectoryCleanup.ManifestFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var previousFiles = InstallDirectoryCleanup.ReadManifest(installDirectory)
                .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            string backup = Path.Combine(recoveryRoot ?? Path.Combine(Path.GetTempPath(), "V-Notch"), Guid.NewGuid().ToString("N") + ".recovery");
            SafeFileDeletion.ValidatePath(backup);
            using var backupLease = AppFileCleanupService.Protect(backup);
            Directory.CreateDirectory(backup);
            var originals = new Dictionary<string, string?>();
            var unownedBackups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool committed = false;
            bool recoveryFailed = false;
            try
            {
                reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.closingRunning"), IsIndeterminate: true));
                await callbacks.StopRunningInstances().ConfigureAwait(false);

                reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.preparingFolder"), IsIndeterminate: true));
                Directory.CreateDirectory(installDirectory);

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
                    if (!string.IsNullOrEmpty(destinationFolder)) InstallDirectoryCleanup.ValidateDirectory(destinationFolder);
                    if (Path.Exists(destinationFile) && (File.GetAttributes(destinationFile) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Installation cannot replace file links.");
                    if (File.Exists(destinationFile))
                    {
                        string saved = Path.Combine(backup, originals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        File.Copy(destinationFile, saved);
                        originals.Add(destinationFile, saved);
                        if (!previousFiles.TryGetValue(relativePath, out var previous) ||
                            !InstallDirectoryCleanup.HashFile(saved).Equals(previous.Sha256, StringComparison.OrdinalIgnoreCase))
                            unownedBackups.Add(saved);
                    }
                    else originals.Add(destinationFile, null);
                    File.WriteAllText(Path.Combine(backup, "restore-map.json"), System.Text.Json.JsonSerializer.Serialize(originals));
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

                string manifest = Path.Combine(installDirectory, InstallDirectoryCleanup.ManifestFileName);
                if (!originals.ContainsKey(manifest))
                {
                    string? saved = File.Exists(manifest) ? Path.Combine(backup, "manifest") : null;
                    if (saved != null) File.Copy(manifest, saved);
                    originals.Add(manifest, saved);
                }
                File.WriteAllText(Path.Combine(backup, "restore-map.json"), System.Text.Json.JsonSerializer.Serialize(originals));
                var ownedFiles = files.Select(file => Path.GetRelativePath(sourceDirectory, file))
                    .Select(path => new InstallDirectoryCleanup.InstalledFile(path, InstallDirectoryCleanup.HashFile(Path.Combine(installDirectory, path))))
                    .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var previous in previousFiles.Values)
                {
                    string path = Path.Combine(installDirectory, previous.Path);
                    if (!ownedFiles.ContainsKey(previous.Path) && File.Exists(path) &&
                        InstallDirectoryCleanup.HashFile(path).Equals(previous.Sha256, StringComparison.OrdinalIgnoreCase))
                        ownedFiles.Add(previous.Path, previous);
                }
                File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(new InstallDirectoryCleanup.InstallManifest(2, ownedFiles.Values.ToArray())));
                committed = true;

                var installedExePath = Path.Combine(installDirectory, AppExeName);
                reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.creatingShortcuts"), files.Length, files.Length));
                callbacks.CreateShortcuts(installedExePath, installDirectory);

                reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.savingStartup"), files.Length, files.Length));
                callbacks.ConfigureStartup(installedExePath, options.StartWithWindows);

                reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.registeringUninstall"), files.Length, files.Length));
                callbacks.RegisterUninstall(installedExePath, installDirectory);

                reportProgress(new SetupProgressInfo(VNotch.Services.Loc.Get("setup.install.savingPreferences"), files.Length, files.Length));
                callbacks.SaveSettings(options.Language);
            }
            catch (Exception installError) when (!committed)
            {
                var failures = new List<Exception> { installError };
                foreach (var original in originals.Reverse())
                {
                    try
                    {
                        if (original.Value == null) File.Delete(original.Key);
                        else
                        {
                            if (File.Exists(original.Key)) File.SetAttributes(original.Key, FileAttributes.Normal);
                            File.Copy(original.Value, original.Key, true);
                        }
                    }
                    catch (Exception restoreError) { failures.Add(restoreError); }
                }
                if (failures.Count > 1)
                {
                    recoveryFailed = true;
                    throw new AggregateException($"Installation rollback failed. Recovery copies remain at {backup}.", failures);
                }
                throw;
            }
            finally
            {
                if (!recoveryFailed)
                {
                    try
                    {
                        if (committed && unownedBackups.Count > 0)
                        {
                            foreach (string saved in originals.Values.OfType<string>())
                                if (!unownedBackups.Contains(saved)) SafeFileDeletion.DeleteFile(saved);
                            File.WriteAllText(Path.Combine(backup, "restore-map.json"), System.Text.Json.JsonSerializer.Serialize(
                                originals.Where(pair => pair.Value != null && unownedBackups.Contains(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value)));
                            RuntimeLog.Warn("SETUP", $"Replaced unverified files were preserved at {backup}.");
                        }
                        else SafeFileDeletion.DeleteDirectory(backup);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Warn("SETUP", "Installation recovery folder cleanup deferred."); }
                }
            }
        });
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            SafeFileDeletion.ValidatePath(entry);
            if (Directory.Exists(entry))
                foreach (string file in EnumeratePayloadFiles(entry)) yield return file;
            else yield return entry;
        }
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

    public static void RunUninstallFlow(bool silent = false)
    {
        var installDirectory = InstallDirectoryCleanup.ValidateDirectory(AppContext.BaseDirectory);
        var driveRoot = Path.GetPathRoot(installDirectory);
        bool isDriveRoot = string.Equals(driveRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            installDirectory, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(installDirectory) || isDriveRoot || !File.Exists(Path.Combine(installDirectory, AppExeName)))
        {
            if (!silent)
            {
                MessageBox.Show(
                    "Invalid installation directory for uninstall.",
                    Loc.Get("setup.uninstall.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Application.Current.Shutdown(1);
            return;
        }

        if (!silent)
        {
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
        }

        if (RequiresAdministratorForInstallPath(installDirectory) && !IsRunningAsAdministrator())
        {
            if (!silent)
            {
                MessageBox.Show(
                    Loc.Get("setup.uninstall.adminRequired"),
                    Loc.Get("setup.windowTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Application.Current.Shutdown(1);
            return;
        }

        InstallDirectoryCleanup.CreateStartInfo(installDirectory, Environment.ProcessId);
        StopOtherRunningInstancesAsync().GetAwaiter().GetResult();
        RemoveStartupRegistration();
        RemoveUninstallRegistration();
        try { RemoveShortcuts(); } catch (Exception ex) { Debug.WriteLine($"[Setup] Could not remove shortcuts: {ex.Message}"); }
        RemoveAppData(installDirectory);

        InstallDirectoryCleanup.Schedule(installDirectory);

        Application.Current.Shutdown(0);
    }

    private static async Task StopOtherRunningInstancesAsync()
    {
        var currentProcessId = Environment.ProcessId;

        foreach (var runningProcess in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            using var process = runningProcess;
            try
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                await StopRunningInstanceAsync(process).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between enumeration and shutdown.
            }
        }
    }

    internal static Task StopRunningInstanceAsync(Process process) => AppProcessShutdown.StopAsync(process);

    internal static void CopyFileWithRetry(string sourceFile, string destinationFile, int maxRetries = 5,
        Action<int>? delay = null)
    {
        const int ErrorSharingViolation = 32;
        const int ErrorLockViolation = 33;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                return;
            }
            // Only sharing/lock violations can clear without changing the
            // installation request. Missing paths and access denial fail now.
            catch (IOException ex) when (attempt < maxRetries && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
            {
                (delay ?? System.Threading.Thread.Sleep)(800 * (attempt + 1));
            }
        }
    }

    private static void ConfigureStartup(string installedExePath, bool startWithWindows)
        => ConfigureStartup(installedExePath, startWithWindows,
            () => Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"));

    internal static void ConfigureStartup(string installedExePath, bool startWithWindows, Func<RegistryKey?> openKey)
    {
        using var runKey = openKey();
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
            InitializeSettingsFile(settingsPath, language);
        }
        catch (Exception ex)
        {
            // Non-fatal: if initial settings file cannot be written, the app will generate default settings on first launch.
            Debug.WriteLine($"[Setup] Failed to write initial settings: {ex.Message}");
        }
    }

    internal static void InitializeSettingsFile(string settingsPath, string language)
    {
        // Let SettingsService perform its versioned migration and DPAPI handling
        // after launch. Setup must preserve an existing user's file byte for byte.
        if (File.Exists(settingsPath)) return;
        var settings = new Models.NotchSettings { Language = language, SettingsVersion = SettingsMigrator.CurrentVersion };
        var json = System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        using var file = new FileStream(settingsPath, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file);
        writer.Write(json);
    }

    private static void RegisterUninstall(string installedExePath, string installDirectory)
        => RegisterUninstall(installedExePath, installDirectory, path => Registry.CurrentUser.CreateSubKey(path));

    internal static void RegisterUninstall(string installedExePath, string installDirectory, Func<string, RegistryKey?> openKey)
    {
        using var uninstallKey = openKey(UninstallRegistryPath);

        if (uninstallKey == null)
        {
            throw new InvalidOperationException(Loc.Get("setup.install.uninstallRegistryUnavailable"));
        }

        uninstallKey.SetValue("DisplayName", AppName);
        uninstallKey.SetValue("DisplayVersion", AppIntegrityService.GetAppVersion());
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
            uninstallKey.SetValue("QuietUninstallString", $"\"{installedExePath}\" --uninstall --quiet");
        }

        using var appKey = openKey($@"Software\{AppName}");
        appKey?.SetValue("InstallDir", installDirectory);
    }

    private static void RemoveUninstallRegistration()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Best-effort cleanup: ignore permission errors when removing HKCU uninstall entry.
            Debug.WriteLine($"[Setup] Could not remove HKCU uninstall key: {ex.Message}");
        }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Best-effort cleanup: ignore permission errors when running without admin privileges for HKLM.
            Debug.WriteLine($"[Setup] Could not remove HKLM uninstall key: {ex.Message}");
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{AppName}", throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Best-effort cleanup: ignore permission errors when removing HKCU app key.
            Debug.WriteLine($"[Setup] Could not remove HKCU app key: {ex.Message}");
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
            SafeFileDeletion.DeleteFile(desktopShortcutPath);
        }

        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);
        SafeFileDeletion.DeleteShortcutDirectory(startMenuDirectory);
    }

    private static void RemoveAppData(string installDirectory)
    {
        string[] directories =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VNotch")
        ];

        foreach (var dir in directories)
        {
            if (SafeFileDeletion.PathsOverlap(dir, installDirectory)) continue;
            try
            {
                SafeFileDeletion.DeleteDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Setup] Could not remove directory '{dir}': {ex.Message}");
            }
        }

        // Temp/V-Notch can contain recovery copies and user files; never wipe the shared root.

        var legacyTempSetup = Path.Combine(Path.GetTempPath(), "V-Notch-Setup.exe");
        try
        {
            SafeFileDeletion.DeleteFile(legacyTempSetup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Setup] Could not remove temp installer '{legacyTempSetup}': {ex.Message}");
        }
    }

    internal static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
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
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
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
