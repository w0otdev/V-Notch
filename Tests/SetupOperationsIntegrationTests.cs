using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupOperationsIntegrationTests
{
    [Fact]
    public async Task HiddenAppGetsTimeToFlushBeforeInstallerOrUninstallerStopsIt()
    {
        using var fixture = new Fixture();
        string ready = Path.Combine(fixture.Root, "ready.txt");
        string flushed = Path.Combine(fixture.Root, "flushed.txt");
        string script = Path.Combine(fixture.Root, "hidden-app.ps1");
        File.WriteAllText(script, """
            Add-Type -AssemblyName System.Windows.Forms
            $form = New-Object System.Windows.Forms.Form
            $null = $form.Handle
            $form.Add_FormClosed({
                [IO.File]::WriteAllText($env:VNOTCH_FLUSHED, 'pending user data saved')
                [System.Windows.Forms.Application]::ExitThread()
            })
            [IO.File]::WriteAllText($env:VNOTCH_READY, 'ready')
            [System.Windows.Forms.Application]::Run()
            """);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File", script }) start.ArgumentList.Add(argument);
        start.Environment["VNOTCH_READY"] = ready;
        start.Environment["VNOTCH_FLUSHED"] = flushed;
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready)) await Task.Delay(25, timeout.Token);
            Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
            await AppProcessShutdown.StopAsync(process);
            Assert.Equal("pending user data saved", File.ReadAllText(flushed));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task StoppingTheOldAppPreservesItsSetupChildProcess()
    {
        using var fixture = new Fixture();
        string childPidPath = Path.Combine(fixture.Root, "child.pid");
        string scriptPath = Path.Combine(fixture.Root, "parent.ps1");
        // A real process tree reproduces an updater launching the setup wizard.
        File.WriteAllText(scriptPath, """
            param($PidPath)
            $child = Start-Process -FilePath $PSHOME\powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru
            Add-Type -AssemblyName System.Windows.Forms
            $form = New-Object System.Windows.Forms.Form
            $null = $form.Handle
            $form.Add_FormClosed({ [System.Windows.Forms.Application]::ExitThread() })
            [IO.File]::WriteAllText("$PidPath.tmp", [string]$child.Id)
            [IO.File]::Move("$PidPath.tmp", $PidPath)
            [System.Windows.Forms.Application]::Run()
            """);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, childPidPath })
            start.ArgumentList.Add(argument);
        using var parent = Process.Start(start)!;
        Process? child = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(childPidPath)) await Task.Delay(25, timeout.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childPidPath, timeout.Token)));
            await SetupOperations.StopRunningInstanceAsync(parent);
            Assert.True(parent.HasExited);
            Assert.False(child.HasExited, "Stopping the installed app must not terminate the setup it launched.");
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
            if (child != null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                child.Dispose();
            }
            await parent.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task UnresponsiveAppIsNeverForceKilledBySetup()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => AppProcessShutdown.StopAsync(process));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallationCopiesItsPayloadAndReportsEachSystemRegistrationInOrder(bool startup)
    {
        using var fixture = new Fixture();
        string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        string install = Path.Combine(fixture.Root, "installed");
        Directory.CreateDirectory(Path.Combine(source, "Assets"));
        await File.WriteAllTextAsync(Path.Combine(source, "V-Notch.exe"), "fixture executable");
        await File.WriteAllTextAsync(Path.Combine(source, "Assets", "fixture.txt"), "fixture asset");
        var calls = new List<string>();
        var progress = new List<SetupProgressInfo>();
        string installedExe = Path.Combine(install, "V-Notch.exe");
        var callbacks = new SetupInstallCallbacks(
            () => { calls.Add("stop"); return Task.CompletedTask; },
            (exe, folder) => { Assert.Equal(installedExe, exe); Assert.Equal(install, folder); Assert.True(File.Exists(exe)); calls.Add("shortcuts"); },
            (exe, start) => { Assert.Equal(installedExe, exe); Assert.Equal(startup, start); calls.Add("startup"); },
            (exe, folder) => { Assert.Equal(installedExe, exe); Assert.Equal(install, folder); calls.Add("uninstall"); },
            language => { Assert.Equal("vi", language); calls.Add("settings"); });
        await SetupOperations.InstallAsync(new(source, install, startup, "vi"), progress.Add, callbacks);
        Assert.Equal("fixture executable", await File.ReadAllTextAsync(installedExe));
        Assert.Equal("fixture asset", await File.ReadAllTextAsync(Path.Combine(install, "Assets", "fixture.txt")));
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
        var callbacks = new SetupInstallCallbacks(() => { registrations++; return Task.CompletedTask; }, (_, _) => registrations++, (_, _) => registrations++, (_, _) => registrations++, _ => registrations++);
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
        using (var persistentLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => SetupOperations.CopyFileWithRetry(source, destination, 2, delays.Add));
            Assert.Equal(new[] { 800, 1600 }, delays);
        }
        delays.Clear();
        Assert.Throws<FileNotFoundException>(() => SetupOperations.CopyFileWithRetry(Path.Combine(fixture.Root, "absent"), destination, 2, delays.Add));
        Assert.Empty(delays);
        delays.Clear();
        // File.Copy reports a directory destination as an IO or access error,
        // depending on the Windows/.NET version. Neither is a transient lock.
        var failure = Record.Exception(() => SetupOperations.CopyFileWithRetry(source, fixture.Root, 1, delays.Add));
        Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected copy failure: {failure}");
        Assert.Empty(delays);
    }

    [Fact]
    public void AccessDeniedDoesNotRetryOrOverwriteTheDestination()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.Root, "source.txt");
        string destination = Path.Combine(fixture.Root, "destination.txt");
        File.WriteAllText(source, "new payload");
        File.WriteAllText(destination, "keep existing payload");
        File.SetAttributes(destination, FileAttributes.ReadOnly);
        var delays = new List<int>();
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => SetupOperations.CopyFileWithRetry(source, destination, delay: delays.Add));
            Assert.Empty(delays);
            Assert.Equal("keep existing payload", File.ReadAllText(destination));
        }
        finally { File.SetAttributes(destination, FileAttributes.Normal); }
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
            Assert.Equal("w0otdev", key.GetValue("Publisher"));
            Assert.Equal(fixture.Root, key.GetValue("InstallLocation"));
            Assert.Equal(executable, key.GetValue("DisplayIcon"));
            Assert.Equal(dedicated ? $"\"{uninstaller}\"" : $"\"{executable}\" --uninstall", key.GetValue("UninstallString"));
            Assert.Equal(dedicated ? $"\"{uninstaller}\" /S" : $"\"{executable}\" --uninstall --quiet", key.GetValue("QuietUninstallString"));
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
