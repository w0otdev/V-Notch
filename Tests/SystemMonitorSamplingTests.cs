using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SystemMonitorSamplingTests
{
    [Fact]
    public void NativeSamplesStayWithinPhysicalBoundsAndStopPublishingAfterStop() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var module = new SystemMonitorModule();
        var samples = new List<SystemMonitorInfo>();
        module.StatsUpdated += (_, info) => samples.Add(info);
        module.Start();
        Assert.True(module.IsRunning);
        // Deliberate wall-clock gap: the module only computes CPU (>= 50 ms) and network
        // (> 100 ms) deltas once enough Stopwatch time has passed since the previous sample.
        // Measure the gap with the same clock rather than trusting Task.Delay granularity.
        await WaitForSamplingIntervalAsync(ct);
        module.Tick();
        await WaitForSamplingIntervalAsync(ct);
        module.Tick();
        Assert.True(samples.Count >= 3);
        Assert.All(samples, sample =>
        {
            Assert.InRange(sample.CpuPercent, 0, 100);
            Assert.InRange(sample.RamPercent, 0, 100);
            Assert.True(sample.RamTotalBytes > 0);
            Assert.InRange(sample.RamUsedBytes, 0UL, sample.RamTotalBytes);
            Assert.True(double.IsFinite(sample.NetDownBytesPerSec));
            Assert.True(double.IsFinite(sample.NetUpBytesPerSec));
            Assert.True(sample.NetDownBytesPerSec >= 0);
            Assert.True(sample.NetUpBytesPerSec >= 0);
        });
        module.Stop();
        int count = samples.Count;
        module.Tick();
        Assert.Equal(count, samples.Count);
        module.Start();
        Assert.Equal(count + 1, samples.Count);
        module.Dispose();
        module.Tick();
        module.Start();
        Assert.Equal(count + 1, samples.Count);
    });

    private static Task WaitForSamplingIntervalAsync(CancellationToken ct)
    {
        var gap = System.Diagnostics.Stopwatch.StartNew();
        return TestWait.UntilAsync(() => gap.ElapsedMilliseconds >= 200, "system monitor sampling interval", ct);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void TrayIconOwnsItsPixelsAfterNativeHandleIsReleased(int size)
    {
        using var icon = IconGenerator.CreateNotchIcon(size);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        using var pixels = icon.ToBitmap();
        Assert.Equal(255, pixels.GetPixel(size / 2, size / 2).A);
        Assert.Equal(30, pixels.GetPixel(size / 4, size / 4).R);
        Assert.Equal(0, pixels.GetPixel(0, size - 1).A);
        using var stream = new System.IO.MemoryStream();
        icon.Save(stream);
        Assert.True(stream.Length > 0);
    }
}
