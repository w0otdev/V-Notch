using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerLifetimeTests
{
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
