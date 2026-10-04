using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class DiagnosticLogCapturePolicyTests
{
    [Fact]
    public void LogStormIsBoundedAndCaptureResumesInTheNextWindow()
    {
        var policy = new DiagnosticLogCapturePolicy();
        int captured = 0;
        for (int i = 0; i < 10000; i++)
            if (policy.TryCapture(1500)) captured++;
        Assert.Equal(DiagnosticLogCapturePolicy.MaxEntriesPerSecond, captured);
        Assert.True(policy.TryCapture(2000));
    }

    [Fact]
    public void ParallelWritersCannotExceedTheCaptureBudget()
    {
        var policy = new DiagnosticLogCapturePolicy();
        int captured = 0;
        Parallel.For(0, 10000, _ =>
        {
            if (policy.TryCapture(1500)) Interlocked.Increment(ref captured);
        });
        Assert.InRange(captured, 1, DiagnosticLogCapturePolicy.MaxEntriesPerSecond);
    }

    [Fact]
    public void VerboseCaptureExistsOnlyWhileAViewerHoldsALease()
    {
        var service = new PerformanceDiagnosticService(subscribeToRuntimeLog: false);
        service.CaptureRuntimeLogEntry(LogLevel.Info, "TEST", "hidden");
        Assert.DoesNotContain(service.GetRecentServiceLogs(), entry => entry.Message == "hidden");

        var first = service.BeginVerboseServiceLogCapture();
        var second = service.BeginVerboseServiceLogCapture();
        first.Dispose();
        first.Dispose();
        service.CaptureRuntimeLogEntry(LogLevel.Info, "TEST", "visible");
        Assert.Contains(service.GetRecentServiceLogs(), entry => entry.Message == "visible");

        second.Dispose();
        service.CaptureRuntimeLogEntry(LogLevel.Info, "TEST", "closed");
        service.CaptureRuntimeLogEntry(LogLevel.Warn, "TEST", "warning");
        Assert.DoesNotContain(service.GetRecentServiceLogs(), entry => entry.Message == "closed");
        Assert.Contains(service.GetRecentServiceLogs(), entry => entry.Message == "warning");
    }
}
