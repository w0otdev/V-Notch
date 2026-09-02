using System;
using System.Linq;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PerformanceDiagnosticServiceTests
{
    [Fact]
    public void SampleSnapshot_ReturnsValidProcessAndGlobalMetrics()
    {
        var service = PerformanceDiagnosticService.Instance;
        var snapshot = service.SampleSnapshot(
            fps: 60.0,
            hz: 60,
            frameTimeMs: 16.6,
            netDown: 1024,
            netUp: 512);

        Assert.NotNull(snapshot);
        Assert.Equal(60.0, snapshot.Fps);
        Assert.Equal(60, snapshot.RefreshRateHz);
        Assert.Equal(16.6, snapshot.FrameTimeMs);
        Assert.True(snapshot.ProcessWorkingSetBytes > 0, "Process Working Set should be > 0");
        Assert.True(snapshot.ProcessPrivateBytes > 0, "Process Private Bytes should be > 0");
        Assert.True(snapshot.ManagedHeapBytes > 0, "Managed Heap should be > 0");
        Assert.True(snapshot.GlobalRamTotalBytes > 0, "Global RAM Total should be > 0");
    }

    [Fact]
    public void AddLog_MaintainsRingBufferAndRetrieval()
    {
        var service = PerformanceDiagnosticService.Instance;
        service.ClearLogs();

        service.AddLog(PerformanceHealthLevel.Warning, "TEST", "Testing custom diagnostic warning message");
        var logs = service.GetRecentLogs();

        Assert.NotEmpty(logs);
        var last = logs.Last();
        Assert.Equal("TEST", last.Category);
        Assert.Equal("Testing custom diagnostic warning message", last.Message);
        Assert.Equal(PerformanceHealthLevel.Warning, last.Severity);
    }

    [Fact]
    public void AnomalyDetection_FlagsHeavyFpsDrop()
    {
        var service = PerformanceDiagnosticService.Instance;
        var snapshot = service.SampleSnapshot(
            fps: 15.0,
            hz: 144,
            frameTimeMs: 66.6,
            netDown: 0,
            netUp: 0);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.HealthLevel == PerformanceHealthLevel.Warning || snapshot.HealthLevel == PerformanceHealthLevel.Critical);
        Assert.Contains("FPS", snapshot.HealthStatusSummary);
    }
}
