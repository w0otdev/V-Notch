using System.Text.Json;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

public sealed class AiUsageParsingBoundaryTests
{
    [Theory]
    [InlineData("-1", "null", false)]
    [InlineData("100.01", "null", false)]
    [InlineData("1e400", "null", false)]
    [InlineData("45.5", "253402300799", true)]
    [InlineData("45.5", "253402300800", false)]
    [InlineData("45.5", "1.5", false)]
    [InlineData("45.5", "null", true)]
    public void ProviderNumbersAreBoundedBeforeCreatingDisplayWindows(string percent, string reset, bool valid)
    {
        using var json = JsonDocument.Parse($"{{\"rateLimits\":{{\"primary\":{{\"usedPercent\":{percent},\"resetsAt\":{reset}}}}}}}");
        var result = AiUsageParsing.Codex(json.RootElement, DateTimeOffset.UnixEpoch, null);
        Assert.Equal(valid ? 1 : 0, result.Windows!.Count);
    }

    [Fact]
    public void BucketCountIsBoundedAndCodexKeepsPriorityWithinTheAcceptedPrefix()
    {
        var buckets = Enumerable.Range(0, 25).Select(i => $"\"{(i == 10 ? "codex" : "bucket" + i)}\":{{\"primary\":{{\"usedPercent\":{i}}}}}");
        using var json = JsonDocument.Parse("{\"rateLimitsByLimitId\":{" + string.Join(',', buckets) + "}}");
        var result = AiUsageParsing.Codex(json.RootElement, DateTimeOffset.UnixEpoch, null);
        Assert.Equal(16, result.Windows!.Count);
        Assert.Equal(10, result.Windows![0].UsedPercent);
    }
}
