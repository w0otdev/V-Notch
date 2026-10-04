using System.Reflection;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowTitleScannerBackoffTests
{
    [Fact]
    public void ThrottledPlaybackReusesCacheForLongerThanNormalPlayback()
    {
        using var scanner = new WindowTitleScanner();
        var cached = new List<string> { "Cached - YouTube" };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WindowTitleScanner).GetField("_cachedWindowTitles", flags)!.SetValue(scanner, cached);
        typeof(WindowTitleScanner).GetField("_lastWindowEnumTime", flags)!.SetValue(scanner, DateTime.UtcNow.AddMilliseconds(-1000));
        Assert.Same(cached, scanner.GetAllWindowTitles(isThrottled: true));
        Assert.NotSame(cached, scanner.GetAllWindowTitles(isThrottled: false));
    }
}
