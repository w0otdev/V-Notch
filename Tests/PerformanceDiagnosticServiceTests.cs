using System;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Globalization;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PerformanceDiagnosticServiceTests
{
    [Fact]
    public void MemoryAlertsKeepDecimalMeasurementsAndDoNotInventACause()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var service = new PerformanceDiagnosticService(subscribeToRuntimeLog: false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(PerformanceDiagnosticService);
            var metricsType = type.GetNestedType("AnomalyMetrics", BindingFlags.NonPublic)!;
            object metrics = Activator.CreateInstance(metricsType)!;
            metricsType.GetProperty("ManagedHeapBytes")!.SetValue(metrics, (long)(63.4 * 1048576));
            metricsType.GetProperty("Gen2")!.SetValue(metrics, int.MaxValue);
            type.GetMethod("CheckGcGen2", flags)!.Invoke(service, [metrics, DateTime.Now]);
            var gc = service.GetRecentLogs().Single(x => x.Category == "BOTTLENECK-GC");
            Assert.Contains("63.4 MB", gc.Message);
            Assert.DoesNotContain("Cause:", gc.Message);

            type.GetField("_lastMemSampleTicks", flags)!.SetValue(service, Stopwatch.GetTimestamp() - 2 * Stopwatch.Frequency);
            type.GetField("_lastPrivateBytes", flags)!.SetValue(service, (ulong)(500 * 1048576));
            metricsType.GetProperty("ProcPrivateBytes")!.SetValue(metrics, (ulong)(564 * 1048576));
            type.GetMethod("CheckMemorySurge", flags)!.Invoke(service, [metrics, 20.0, DateTime.Now]);
            var ram = service.GetRecentLogs().Single(x => x.Category == "BOTTLENECK-RAM");
            Assert.Contains("within 2.0s", ram.Message);
            Assert.Contains("MB/s", ram.Message);
            Assert.DoesNotContain("Cause:", ram.Message);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

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
        var last = logs[^1];
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
