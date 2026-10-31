using System;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MetricsAndAudioMixerCacheTests
{
    [Fact]
    public async Task ConcurrentMetricSampling_StaysBoundedWithoutNaNOrNegativeValues()
    {
        const int threadCount = 4;
        const int iterationsPerThread = 25;

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    var diag = PerformanceDiagnosticService.Instance.SampleSnapshot(120.0, 120, 8.3, 0.0, 0.0);
                    Assert.True(double.IsFinite(diag.ProcessCpuPercent));
                    Assert.InRange(diag.ProcessCpuPercent, 0.0, 100.0);
                    Assert.True(double.IsFinite(diag.GlobalCpuPercent));
                    Assert.InRange(diag.GlobalCpuPercent, 0.0, 100.0);
                    Assert.True(double.IsFinite(diag.AllocBytesPerSec));
                    Assert.True(diag.AllocBytesPerSec >= 0.0);
                    Assert.False(string.IsNullOrWhiteSpace(PerformanceDiagnosticService.Instance.CurrentHealthSummary));

                    var gpu = await GpuMonitorService.Instance.GetMetricsAsync(120.0, 120, 0.0, 0.0);
                    Assert.True(double.IsFinite(gpu.ProcessCpuPercent));
                    Assert.InRange(gpu.ProcessCpuPercent, 0.0, 100.0);
                    Assert.True(double.IsFinite(gpu.GlobalCpuPercent));
                    Assert.InRange(gpu.GlobalCpuPercent, 0.0, 100.0);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void AudioMixerMetadataCache_ResolvesOncePerActiveSession_AndPrunesDeadPids()
    {
        using var service = new AudioMixerService();
        int nameResolveCalls = 0;
        int iconResolveCalls = 0;

        (string DisplayName, string? ExePath) FakeNameResolver(uint pid, string rawId)
        {
            Interlocked.Increment(ref nameResolveCalls);
            return ($"App-{pid}-{rawId}", $@"C:\Apps\app-{pid}.exe");
        }

        System.Windows.Media.ImageSource? FakeIconResolver(string exePath, uint pid)
        {
            Interlocked.Increment(ref iconResolveCalls);
            return null;
        }

        var first = service.GetOrResolveSessionMetadata(4242u, "raw-session-1", FakeNameResolver, FakeIconResolver);
        var second = service.GetOrResolveSessionMetadata(4242u, "raw-session-1", FakeNameResolver, FakeIconResolver);

        Assert.Equal("App-4242-raw-session-1", first.DisplayName);
        Assert.Equal(@"C:\Apps\app-4242.exe", first.ExePath);
        Assert.Equal(first, second);
        Assert.Equal(1, nameResolveCalls);
        Assert.Equal(1, iconResolveCalls);

        _ = service.GetOrResolveSessionMetadata(7777u, "raw-session-2", FakeNameResolver, FakeIconResolver);
        Assert.Equal(2, nameResolveCalls);
        Assert.Equal(2, service.MetadataCacheCount);

        ReadOnlySpan<uint> activePids = [4242u];
        service.PruneMetadataCache(activePids);
        Assert.Equal(1, service.MetadataCacheCount);

        // Cached active PID 4242u is retained without re-invoking the resolver
        _ = service.GetOrResolveSessionMetadata(4242u, "raw-session-1", FakeNameResolver, FakeIconResolver);
        Assert.Equal(2, nameResolveCalls);

        // Pruned dead PID 7777u triggers a fresh resolve when seen again
        _ = service.GetOrResolveSessionMetadata(7777u, "raw-session-2", FakeNameResolver, FakeIconResolver);
        Assert.Equal(3, nameResolveCalls);
    }
}
