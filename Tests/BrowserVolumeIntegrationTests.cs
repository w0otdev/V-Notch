using System.Diagnostics;
using System.IO;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class BrowserVolumeIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task InstalledChromiumBrowserVolumeChangesReachItsAudioProcess()
    {
        string? executable = Environment.GetEnvironmentVariable("VNOTCH_VOLUME_TEST_BROWSER");
        executable ??= new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        }.FirstOrDefault(File.Exists);
        if (executable == null) { output.WriteLine("No Chromium browser installed; live check unavailable."); return; }

        string processName = Path.GetFileNameWithoutExtension(executable);
        var existingPids = Process.GetProcessesByName(processName).Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        string testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "volume-browser-tests"));
        string profile = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        string page = Path.Combine(profile, "volume-test.html");
        await File.WriteAllTextAsync(page, """
            <!doctype html><title>V-Notch volume integration test</title>
            <script>
            const context = new AudioContext();
            const oscillator = context.createOscillator();
            const gain = context.createGain();
            gain.gain.value = 0;
            oscillator.connect(gain).connect(context.destination);
            oscillator.start();
            context.resume();
            </script>
            """);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--headless=new", "--no-first-run", "--disable-background-networking",
            "--autoplay-policy=no-user-gesture-required", "--user-data-dir=" + profile, new Uri(page).AbsoluteUri }) start.ArgumentList.Add(argument);
        using var browser = Process.Start(start)!;
        try
        {
            HashSet<uint> testPids = [];
            using var service = new MediaSessionVolumeService(WindowsMediaAudioSessions.ReadAllEndpoints, _ => testPids, () => DateTime.UtcNow);
            // Only the new isolated browser processes can match this artificial source ID.
            const string source = "VNotch.ControlledBrowser.exe";
            bool ready = false;
            for (int attempt = 0; attempt < 50 && !ready; attempt++)
            {
                testPids = Process.GetProcessesByName(processName).Select(p => { using (p) return (uint)p.Id; })
                    .Where(pid => !existingPids.Contains(pid)).ToHashSet();
                ready = service.TryGetVolume(source, out _, out _);
                if (!ready) await Task.Delay(100);
            }
            Assert.True(ready, "The isolated browser did not create a Windows audio session.");
            using var sessions = WindowsMediaAudioSessions.ReadAllEndpoints();
            var controlled = sessions.Sessions.First(s => testPids.Contains(s.GetProcessID) && s.State == 1);
            Assert.Contains(controlled.GetProcessID, MediaSessionVolumeService.GetProcessIds(processName + ".exe"));
            using var lease = controlled.SimpleAudioVolume;
            float original = lease.Volume;
            bool muted = lease.Mute;
            try
            {
                Assert.True(service.TrySetVolume(source, .25f));
                Assert.Equal(.25f, lease.Volume, 4);
                Assert.True(service.TrySetVolume(source, .65f));
                Assert.Equal(.65f, lease.Volume, 4);
                output.WriteLine($"{executable} {FileVersionInfo.GetVersionInfo(executable).FileVersion}: native volume .25 -> .65 confirmed");
            }
            finally { lease.Volume = original; lease.Mute = muted; }
        }
        finally
        {
            if (!browser.HasExited) browser.Kill(entireProcessTree: true);
            await browser.WaitForExitAsync();
            // Restrict recursive cleanup to this test's own profile under its fixed output directory.
            if (Path.GetDirectoryName(Path.GetFullPath(profile)) == testRoot)
            {
                try { Directory.Delete(profile, recursive: true); }
                catch (IOException) { /* A browser child may still be releasing a profile file. */ }
            }
        }
    }
}
