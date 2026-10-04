using System.Diagnostics;
using System.IO;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class InstallDirectoryCleanupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative-folder")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\folder\..")]
    [InlineData(@"\\192.0.2.1\share\app")]
    [InlineData(@"\/192.0.2.1/share/app")]
    [InlineData(@"/\192.0.2.1/share/app")]
    [InlineData(@"\\?\C:\app")]
    [InlineData("C:\\app\r\ncalc.exe")]
    [InlineData("C:\\app|calc.exe")]
    [InlineData("C:\\app\"bad")]
    public void Cleanup_RejectsUnsafeDirectoriesBeforeStartingHelper(string directory) =>
        Assert.Throws<ArgumentException>(() => InstallDirectoryCleanup.CreateStartInfo(directory, Environment.ProcessId));

    [Fact]
    public void Cleanup_DoesNotInterpolateLegalPathMetacharactersIntoScript()
    {
        string parent = Directory.CreateTempSubdirectory("vnotch-cleanup-").FullName;
        string directory = Path.Combine(parent, "V-Notch & %COMSPEC% ^ ! ' Unicode_ế");
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "V-Notch.exe");
        File.WriteAllText(marker, "test marker; never executed");
        try
        {
            var start = InstallDirectoryCleanup.CreateStartInfo(directory, Environment.ProcessId);
            Assert.Equal(directory, start.Environment["VNOTCH_CLEANUP_DIRECTORY"]);
            string script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1]));
            Assert.DoesNotContain(directory, script);
            Assert.Contains("$env:VNOTCH_CLEANUP_DIRECTORY", script);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal("powershell.exe", Path.GetFileName(start.FileName));
        }
        finally
        {
            File.Delete(marker);
            Directory.Delete(directory);
            Directory.Delete(parent);
        }
    }

    [Fact]
    public async Task Cleanup_WaitsForOwnerAndDeletesOnlyItsTarget()
    {
        string parent = Directory.CreateTempSubdirectory("vnotch-cleanup-integration-").FullName;
        string directory = Path.Combine(parent, "V-Notch & %literal% ^ ! ' ế");
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "V-Notch.exe");
        string sentinel = Path.Combine(parent, "keep.txt");
        File.WriteAllText(marker, "test marker; never executed");
        File.WriteAllText(sentinel, "must survive cleanup");
        string releaseName = $"Local\\VNotchOwnerRelease-{Guid.NewGuid():N}";
        string readyName = $"Local\\VNotchOwnerReady-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var ownerStart = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$release = [System.Threading.EventWaitHandle]::OpenExisting($env:VNOTCH_OWNER_RELEASE); " +
            "$ready = [System.Threading.EventWaitHandle]::OpenExisting($env:VNOTCH_OWNER_READY); " +
            "$null = $ready.Set(); $null = $release.WaitOne()" })
            ownerStart.ArgumentList.Add(argument);
        ownerStart.Environment["VNOTCH_OWNER_RELEASE"] = releaseName;
        ownerStart.Environment["VNOTCH_OWNER_READY"] = readyName;
        using var owner = Process.Start(ownerStart)!;
        using var helper = Process.Start(InstallDirectoryCleanup.CreateStartInfo(directory, owner.Id))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(10))),
                "Owner did not initialize its release gate.");
            await Task.Delay(700, timeout.Token);
            Assert.False(owner.HasExited);
            Assert.True(File.Exists(marker));
            release.Set();
            await helper.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, helper.ExitCode);
            Assert.False(Directory.Exists(directory));
            Assert.Equal("must survive cleanup", File.ReadAllText(sentinel));
        }
        finally
        {
            release.Set();
            if (!owner.HasExited) owner.Kill();
            if (!helper.HasExited) helper.Kill();
            // Only exact paths created by this test; never recursively delete the parent.
            if (File.Exists(marker)) File.Delete(marker);
            if (Directory.Exists(directory)) Directory.Delete(directory);
            File.Delete(sentinel);
            Directory.Delete(parent);
        }
    }

    [Fact]
    public void Cleanup_RejectsDirectoryJunctions()
    {
        string parent = Directory.CreateTempSubdirectory("vnotch-cleanup-link-").FullName;
        string target = Path.Combine(parent, "target");
        string link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$null = New-Item -ItemType Junction -Path $env:VNOTCH_TEST_LINK -Target $env:VNOTCH_TEST_TARGET" })
            start.ArgumentList.Add(argument);
        start.Environment["VNOTCH_TEST_LINK"] = link;
        start.Environment["VNOTCH_TEST_TARGET"] = target;
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<ArgumentException>(() => InstallDirectoryCleanup.ValidateDirectory(link)); }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target);
            Directory.Delete(parent);
        }
    }
}
