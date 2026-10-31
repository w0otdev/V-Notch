using System.Reflection;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerBackoffTests
{
    [Fact]
    public void CallersCannotMutateFreshOrCachedTitleSnapshots()
    {
        using var scanner = new WindowTitleScanner();
        var first = scanner.GetAllWindowTitles(false);
        var expected = first.ToArray();
        first.Clear();
        first.Add("Injected - YouTube");
        var second = scanner.GetAllWindowTitles(false);
        Assert.Equal(expected, second);
        second.Add("Another injected title");
        Assert.Equal(expected, scanner.GetAllWindowTitles(false));
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ThrottledPlaybackReusesCacheForLongerThanNormalPlayback()
    {
        using var scanner = new WindowTitleScanner();
        var cached = new List<string> { "Cached - YouTube" };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WindowTitleScanner).GetField("_cachedWindowTitles", flags)!.SetValue(scanner, cached);
        typeof(WindowTitleScanner).GetField("_lastWindowEnumTime", flags)!.SetValue(scanner, DateTime.UtcNow.AddMilliseconds(-1000));
        var timestamp = typeof(WindowTitleScanner).GetField("_lastWindowEnumTime", flags)!;
        var originalTimestamp = timestamp.GetValue(scanner);
        Assert.Equal(cached, scanner.GetAllWindowTitles(isThrottled: true));
        Assert.Equal(originalTimestamp, timestamp.GetValue(scanner));
        scanner.GetAllWindowTitles(isThrottled: false);
        Assert.NotEqual(originalTimestamp, timestamp.GetValue(scanner));
    }

    [Fact]
    public void PipLookupForNonOwnerProcess_CachesResultWithinTtlAndResetsOnInvalidate()
    {
        using var scanner = new WindowTitleScanner();
        int globalScans = 0;
        int spotifyScans = 0;

        scanner.PipWindowFinder = (out IntPtr hwnd, out string title, string? processName) =>
        {
            if (processName == null)
            {
                globalScans++;
                hwnd = (IntPtr)0x1234;
                title = "Picture in picture";
                return true;
            }

            if (string.Equals(processName, "spotify", StringComparison.OrdinalIgnoreCase))
            {
                spotifyScans++;
            }

            hwnd = IntPtr.Zero;
            title = string.Empty;
            return false;
        };

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.False(scanner.IsPipActive("spotify"));
        typeof(WindowTitleScanner).GetField("_cachedPipWindowProcessName", flags)!.SetValue(scanner, "chrome");

        Assert.False(scanner.IsPipActive("spotify"));
        Assert.False(scanner.IsPipActive("SPOTIFY"));
        Assert.False(scanner.TryGetPipWindow(out IntPtr pipHwnd, out string pipTitle, "spotify"));
        Assert.Equal(IntPtr.Zero, pipHwnd);
        Assert.Equal(string.Empty, pipTitle);

        Assert.Equal(1, globalScans);
        Assert.Equal(1, spotifyScans);

        scanner.InvalidateUrlCaches();
        Assert.False(scanner.IsPipActive("spotify"));
        Assert.Equal(2, globalScans);
        Assert.Equal(2, spotifyScans);
    }
}
