using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupOperationsIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallationCopiesItsPayloadAndReportsEachSystemRegistrationInOrder(bool startup)
    {
        using var fixture = new Fixture();
        string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        string install = Path.Combine(fixture.Root, "installed");
        Directory.CreateDirectory(Path.Combine(source, "Assets"));
        File.WriteAllText(Path.Combine(source, "V-Notch.exe"), "fixture executable");
        File.WriteAllText(Path.Combine(source, "Assets", "fixture.txt"), "fixture asset");
        var calls = new List<string>();
        var progress = new List<SetupProgressInfo>();
        string installedExe = Path.Combine(install, "V-Notch.exe");
        var callbacks = new SetupInstallCallbacks(
            () => calls.Add("stop"),
            (exe, folder) => { Assert.Equal(installedExe, exe); Assert.Equal(install, folder); Assert.True(File.Exists(exe)); calls.Add("shortcuts"); },
            (exe, start) => { Assert.Equal(installedExe, exe); Assert.Equal(startup, start); calls.Add("startup"); },
            (exe, folder) => { Assert.Equal(installedExe, exe); Assert.Equal(install, folder); calls.Add("uninstall"); },
            language => { Assert.Equal("vi", language); calls.Add("settings"); });
        await SetupOperations.InstallAsync(new(source, install, startup, "vi"), progress.Add, callbacks);
        Assert.Equal("fixture executable", File.ReadAllText(installedExe));
        Assert.Equal("fixture asset", File.ReadAllText(Path.Combine(install, "Assets", "fixture.txt")));
        Assert.Equal(new[] { "stop", "shortcuts", "startup", "uninstall", "settings" }, calls);
        Assert.Equal(8, progress.Count);
        Assert.All(progress.Take(2), item => Assert.True(item.IsIndeterminate));
        Assert.Equal(new[] { 1, 2 }, progress.Skip(2).Take(2).Select(item => item.CurrentStep));
        Assert.All(progress.Skip(2), item => Assert.Equal(2, item.TotalSteps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPayloadFailsBeforeStoppingProcessesOrRegisteringTheApplication(bool sourceExists)
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "source");
        if (sourceExists) Directory.CreateDirectory(source);
        int registrations = 0;
        var callbacks = new SetupInstallCallbacks(() => registrations++, (_, _) => registrations++, (_, _) => registrations++, (_, _) => registrations++, _ => registrations++);
        Task operation = SetupOperations.InstallAsync(new(source, Path.Combine(fixture.Root, "installed"), false), _ => registrations++, callbacks);
        if (sourceExists) await Assert.ThrowsAsync<FileNotFoundException>(() => operation);
        else await Assert.ThrowsAsync<DirectoryNotFoundException>(() => operation);
        Assert.Equal(0, registrations);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "installed")));
    }

    [Fact]
    public void CopyRetriesReleaseLockedFilesAndPropagatePersistentFailures()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "source.txt");
        string destination = Path.Combine(fixture.Root, "destination.txt");
        File.WriteAllText(source, "fixture payload");
        using var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        var delays = new List<int>();
        SetupOperations.CopyFileWithRetry(source, destination, 2, delay => { delays.Add(delay); locked.Dispose(); });
        Assert.Equal(new[] { 800 }, delays);
        Assert.Equal("fixture payload", File.ReadAllText(destination));
        delays.Clear();
        Assert.Throws<FileNotFoundException>(() => SetupOperations.CopyFileWithRetry(Path.Combine(fixture.Root, "absent"), destination, 2, delays.Add));
        Assert.Equal(new[] { 800, 1600 }, delays);
        delays.Clear();
        Assert.Throws<UnauthorizedAccessException>(() => SetupOperations.CopyFileWithRetry(source, fixture.Root, 1, delays.Add));
        Assert.Equal(new[] { 800 }, delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationWritesTheDedicatedOrFallbackUninstallerIntoIsolatedRegistryKeys(bool dedicated)
    {
        using var fixture = new Fixture();
        string registryRoot = @"Software\VNotch.Tests\" + Guid.NewGuid().ToString("N");
        string executable = Path.Combine(fixture.Root, "V-Notch.exe");
        string uninstaller = Path.Combine(fixture.Root, "uninstall.exe");
        if (dedicated) File.WriteAllText(uninstaller, "fixture");
        try
        {
            SetupOperations.RegisterUninstall(executable, fixture.Root,
                path => Registry.CurrentUser.CreateSubKey(registryRoot + (path.Contains(@"\Uninstall\", StringComparison.Ordinal) ? @"\uninstall" : @"\app")));
            using var key = Registry.CurrentUser.OpenSubKey(registryRoot + @"\uninstall");
            Assert.Equal("V-Notch", key!.GetValue("DisplayName"));
            Assert.Equal("rainaku", key.GetValue("Publisher"));
            Assert.Equal(fixture.Root, key.GetValue("InstallLocation"));
            Assert.Equal(executable, key.GetValue("DisplayIcon"));
            Assert.Equal(dedicated ? $"\"{uninstaller}\"" : $"\"{executable}\" --uninstall", key.GetValue("UninstallString"));
            Assert.Equal(dedicated ? $"\"{uninstaller}\" /S" : $"\"{executable}\" --uninstall", key.GetValue("QuietUninstallString"));
            using var app = Registry.CurrentUser.OpenSubKey(registryRoot + @"\app");
            Assert.Equal(fixture.Root, app!.GetValue("InstallDir"));
            SetupOperations.ConfigureStartup(executable, true, () => Registry.CurrentUser.CreateSubKey(registryRoot + @"\run"));
            using (var run = Registry.CurrentUser.OpenSubKey(registryRoot + @"\run")) Assert.Equal($"\"{executable}\"", run!.GetValue("V-Notch"));
            SetupOperations.ConfigureStartup(executable, false, () => Registry.CurrentUser.CreateSubKey(registryRoot + @"\run"));
            using (var run = Registry.CurrentUser.OpenSubKey(registryRoot + @"\run")) Assert.Null(run!.GetValue("V-Notch"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false); }
    }

    [Fact]
    public void MissingRegistryAccessIsReportedAndShortcutTargetAndWorkingDirectoryArePreserved()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => SetupOperations.ConfigureStartup("fixture.exe", true, () => null));
        Assert.Throws<InvalidOperationException>(() => SetupOperations.RegisterUninstall("fixture.exe", fixture.Root, _ => null));
        string shortcutPath = Path.Combine(fixture.Root, "fixture.lnk");
        string target = Path.Combine(fixture.Root, "fixture.exe");
        SetupOperations.CreateShortcut(shortcutPath, target, fixture.Root);
        Assert.True(File.Exists(shortcutPath));
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        object? shortcutObject = null;
        try
        {
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            Assert.Equal(target, (string)shortcut.TargetPath);
            Assert.Equal(fixture.Root, (string)shortcut.WorkingDirectory);
            Assert.Equal("V-Notch", (string)shortcut.Description);
        }
        finally
        {
            if (shortcutObject != null) Marshal.FinalReleaseComObject(shortcutObject);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("vnotch-setup-contract-").FullName;
        public void Dispose()
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(Root, true);
        }
    }
}
