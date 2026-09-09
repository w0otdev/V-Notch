using System;
using System.Diagnostics;
using System.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerLockTests
{
    [Fact]
    public void TryGetBrowserUrl_ReturnsImmediately_AndDoesNotBlockOtherCallers()
    {
        using var scanner = new WindowTitleScanner();
        using var workerStarted = new ManualResetEventSlim(false);
        using var workerRelease = new ManualResetEventSlim(false);

        scanner.BrowserUrlExtractor = () =>
        {
            workerStarted.Set();
            workerRelease.Wait(TimeSpan.FromSeconds(5));
            return "https://youtube.com/watch?v=dQw4w9WgXcQ";
        };

        // 1. Initial call should return null immediately without waiting for the slow worker
        var sw = Stopwatch.StartNew();
        string? coldUrl = scanner.TryGetBrowserUrl();
        sw.Stop();

        Assert.Null(coldUrl);
        Assert.True(sw.ElapsedMilliseconds < 200, $"TryGetBrowserUrl blocked caller for {sw.ElapsedMilliseconds} ms");

        // Wait for worker to enter the extraction function
        Assert.True(workerStarted.Wait(TimeSpan.FromSeconds(2)), "Worker did not start in time");

        // 2. While worker is blocked inside extractor, other methods needing _cacheLock must NOT block
        sw.Restart();
        _ = scanner.GetAllWindowTitles(isThrottled: false);
        _ = scanner.IsPipActive();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"GetAllWindowTitles / IsPipActive blocked by UIA for {sw.ElapsedMilliseconds} ms");

        // 3. Unblock worker and verify result updates
        workerRelease.Set();

        // Wait for worker to update cache
        SpinWait.SpinUntil(() => scanner.TryGetBrowserUrl() != null, TimeSpan.FromSeconds(3));
        string? warmUrl = scanner.TryGetBrowserUrl();

        Assert.Equal("https://youtube.com/watch?v=dQw4w9WgXcQ", warmUrl);
    }

    [Fact]
    public void InvalidateUrlCaches_DiscardsStaleInFlightScan()
    {
        using var scanner = new WindowTitleScanner();
        using var firstScanStarted = new ManualResetEventSlim(false);
        using var firstScanRelease = new ManualResetEventSlim(false);
        using var firstScanCompleted = new ManualResetEventSlim(false);

        int invocationCount = 0;
        scanner.BrowserUrlExtractor = () =>
        {
            int count = Interlocked.Increment(ref invocationCount);
            if (count == 1)
            {
                firstScanStarted.Set();
                firstScanRelease.Wait(TimeSpan.FromSeconds(5));
                firstScanCompleted.Set();
                return "https://stale-site.com/video";
            }
            return "https://fresh-site.com/video";
        };

        // Trigger first scan
        _ = scanner.TryGetBrowserUrl();
        Assert.True(firstScanStarted.Wait(TimeSpan.FromSeconds(2)), "First scan did not start in time");

        // Invalidate cache while first scan is running in background (increments generation)
        scanner.InvalidateUrlCaches();

        // Release first scan
        firstScanRelease.Set();
        Assert.True(firstScanCompleted.Wait(TimeSpan.FromSeconds(2)), "First scan did not complete");
        string? current = scanner.TryGetBrowserUrl();
        Assert.NotEqual("https://stale-site.com/video", current);

        // Wait for next generation scan to finish
        SpinWait.SpinUntil(() => scanner.TryGetBrowserUrl() == "https://fresh-site.com/video", TimeSpan.FromSeconds(3));
        current = scanner.TryGetBrowserUrl();

        Assert.Equal("https://fresh-site.com/video", current);
    }

    [Fact]
    public void RapidInvocations_DoNotSpawnUnboundedWorkers()
    {
        using var scanner = new WindowTitleScanner();
        using var workerBlocked = new ManualResetEventSlim(false);

        int executionCount = 0;
        scanner.BrowserUrlExtractor = () =>
        {
            Interlocked.Increment(ref executionCount);
            workerBlocked.Wait(TimeSpan.FromSeconds(5));
            return "https://example.com";
        };

        // Call TryGetBrowserUrl 100 times rapidly while worker is blocked
        for (int i = 0; i < 100; i++)
        {
            _ = scanner.TryGetBrowserUrl();
        }

        // Release worker
        workerBlocked.Set();
        SpinWait.SpinUntil(() => scanner.TryGetBrowserUrl() != null, TimeSpan.FromSeconds(3));

        // Verify extractor was NOT executed 100 times
        Assert.True(executionCount <= 2, $"Expected at most 2 executions, got {executionCount}");
    }

    [Fact]
    public void IsSpotifyWebPlayerOpen_ExtractsOutsideLockAndCaches()
    {
        using var scanner = new WindowTitleScanner();
        using var workerDone = new ManualResetEventSlim(false);

        scanner.SpotifyWebPlayerDetector = () =>
        {
            workerDone.Set();
            return true;
        };

        // Initial call returns false immediately
        bool initial = scanner.IsSpotifyWebPlayerOpen();
        Assert.False(initial);

        // Wait for background worker to complete
        Assert.True(workerDone.Wait(TimeSpan.FromSeconds(2)), "Spotify worker did not complete");

        // Subsequent call returns true from cache
        SpinWait.SpinUntil(() => scanner.IsSpotifyWebPlayerOpen(), TimeSpan.FromSeconds(3));
        bool updated = scanner.IsSpotifyWebPlayerOpen();

        Assert.True(updated);
    }
}
