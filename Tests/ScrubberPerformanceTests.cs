using System.Diagnostics;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[CollectionDefinition("Scrubber performance", DisableParallelization = true)]
public sealed class ScrubberPerformanceCollection { }

[Collection("Scrubber performance")]
public sealed class ScrubberPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void OrdinaryLogsReuseTheInputAndHaveNoSteadyStateAllocations()
    {
        string[] logs = [
            "INFO request=42 completed status=200 elapsed=7ms",
            "DEBUG Japanese 日本語 🌸 window width=320 height=50",
            "INFO password control visible; basic view enabled; session ended",
            "DEBUG https://example.com/path?count=3&mode=normal"
        ];
        foreach (string log in logs)
        {
            for (int i = 0; i < 1000; i++) SensitiveDataScrubber.Scrub(log);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            string result = log;
            for (int i = 0; i < 100_000; i++) result = SensitiveDataScrubber.Scrub(log);
            var elapsed = Stopwatch.GetElapsedTime(started);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"normal {log.Length} chars: {elapsed.TotalMilliseconds:F2} ms / 100000 calls; {allocated} bytes");
            Assert.Same(log, result);
            Assert.True(allocated <= 128, $"Unexpected steady-state allocation: {allocated} bytes.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwentyMiBAsciiDumpPreservesSafeContextAndRedactsSecretsAtBothEnds(bool dense)
    {
        // 20 MiB in UTF-8; .NET's UTF-16 input occupies 40 MiB, excluding context.
        string filler = dense ? string.Concat(Enumerable.Repeat("api_key=private-key; ", 1_048_576)) : new string('x', 20 * 1024 * 1024);
        string input = "request=42\npassword='secret with spaces'\n" + filler
            + "\nAuthorization: Basic YWRtaW46MTIz\n at App.Authenticate() status=401";
        SensitiveDataScrubber.Scrub("password=warmup");
        var timer = Stopwatch.StartNew();
        string result = SensitiveDataScrubber.Scrub(input);
        timer.Stop();
        output.WriteLine($"20 MiB dump dense={dense}: {timer.Elapsed.TotalMilliseconds:F2} ms");
        Assert.DoesNotContain("secret with spaces", result);
        Assert.DoesNotContain("YWRtaW46MTIz", result);
        Assert.DoesNotContain("private-key", result);
        Assert.StartsWith("request=42\npassword='[REDACTED]'\n", result);
        Assert.EndsWith("\nAuthorization: Basic [REDACTED]\n at App.Authenticate() status=401", result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Scrubbing took {timer.Elapsed}.");
    }
}
