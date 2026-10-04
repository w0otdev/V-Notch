using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerLifetimeTests
{
    [Fact]
    public async Task SettingsWatcherCreatesANewDirectoryAndSeesTheFirstSettingsFile()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VNotchWatcher-" + Guid.NewGuid().ToString("N"));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.False(System.IO.Directory.Exists(directory));
            using var watcher = WindowTitleScanner.CreateSettingsWatcher(directory, () => changed.TrySetResult());
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(directory, "settings.json"), "{}");
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CallerMutationsCannotChangeTheCachedWindowTitles()
    {
        using var scanner = new WindowTitleScanner();
        var first = scanner.GetAllWindowTitles(isThrottled: true);
        var expected = first.ToArray();
        first.Clear();
        first.Add("caller-owned value");
        Assert.Equal(expected, scanner.GetAllWindowTitles(isThrottled: true));
    }

    [Fact]
    public void TitleOnlyPollingDoesNotStartTheAutomationWorker()
    {
        using var scanner = new WindowTitleScanner();
        _ = scanner.GetAllWindowTitles(isThrottled: false);
        scanner.InvalidateUrlCaches();
        Assert.False(scanner.IsWorkerStarted);
        scanner.Dispose();
        scanner.Dispose();
        Assert.Null(scanner.TryGetBrowserUrl());
        Assert.Null(scanner.TryGetMediaUrlFromAnyBrowser());
        Assert.False(scanner.IsSpotifyWebPlayerOpen());
        Assert.False(scanner.IsWorkerStarted);
    }

    [Fact]
    public void DisposalDoesNotWaitForAnInFlightAutomationCallAndWorkerExitsAfterItReturns()
    {
        using var scanner = new WindowTitleScanner();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scanner.SpotifyWebPlayerDetector = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return true;
        };
        try
        {
            Assert.False(scanner.IsSpotifyWebPlayerOpen());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(scanner.IsWorkerStarted);
            var elapsed = Stopwatch.StartNew();
            scanner.Dispose();
            Assert.True(elapsed.ElapsedMilliseconds < 200);
            Assert.False(scanner.IsSpotifyWebPlayerOpen());
            Assert.Null(scanner.TryGetBrowserUrl());
        }
        finally { release.Set(); }
        Assert.True(SpinWait.SpinUntil(() => !scanner.IsWorkerAlive, TimeSpan.FromSeconds(2)));
        Assert.False(scanner.IsSpotifyWebPlayerOpen());
    }
}
