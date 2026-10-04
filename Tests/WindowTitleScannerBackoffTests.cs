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
}
