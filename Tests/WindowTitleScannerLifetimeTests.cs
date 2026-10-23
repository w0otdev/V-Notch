using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerLifetimeTests
{
    [Fact]
    public void BrowserInspectionRequiresExplicitPermissionAndRevocationStopsAllAutomation()
    {
        bool allowed = false;
        using var scanner = new WindowTitleScanner(() => allowed);
        int calls = 0;
        scanner.BrowserUrlExtractor = () => { Interlocked.Increment(ref calls); return "https://example.com"; };
        scanner.AnyBrowserMediaUrlExtractor = () => { Interlocked.Increment(ref calls); return "https://youtube.com/watch?v=example"; };
        scanner.SpotifyWebPlayerDetector = () => { Interlocked.Increment(ref calls); return true; };
        Assert.Null(scanner.TryGetBrowserUrl());
        Assert.Null(scanner.TryGetMediaUrlFromAnyBrowser());
        Assert.False(scanner.IsSpotifyWebPlayerOpen());
        Assert.False(scanner.IsWorkerStarted);
        Assert.Equal(0, calls);

        allowed = true;
        Assert.True(SpinWait.SpinUntil(() => scanner.TryGetBrowserUrl() != null, TimeSpan.FromSeconds(2)));
        allowed = false;
        Assert.Null(scanner.TryGetBrowserUrl());
        Assert.Null(scanner.TryGetMediaUrlFromAnyBrowser());
        Assert.False(scanner.IsSpotifyWebPlayerOpen());
    }

    [Fact]
    public void RevokingPermissionDiscardsAnInFlightAutomationResult()
    {
        bool allowed = true;
        using var scanner = new WindowTitleScanner(() => Volatile.Read(ref allowed));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scanner.BrowserUrlExtractor = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return "https://private.example";
        };
        Assert.Null(scanner.TryGetBrowserUrl());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Volatile.Write(ref allowed, false);
            Assert.Null(scanner.TryGetBrowserUrl());
        }
        finally { release.Set(); }
        scanner.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !scanner.IsWorkerAlive, TimeSpan.FromSeconds(2)));
        Assert.Null(scanner.TryGetBrowserUrl());
    }

    [Fact]
    public void CallerMutationsCannotChangeTheCachedWindowTitles()
    {
        using var scanner = new WindowTitleScanner(() => true);
        var first = scanner.GetAllWindowTitles(isThrottled: true);
        var expected = first.ToArray();
        first.Clear();
        first.Add("caller-owned value");
        Assert.Equal(expected, scanner.GetAllWindowTitles(isThrottled: true));
    }

    [Fact]
    public void TitleOnlyPollingDoesNotStartTheAutomationWorker()
    {
        using var scanner = new WindowTitleScanner(() => true);
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
        using var scanner = new WindowTitleScanner(() => true);
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
