using System.Reflection;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyIndicatorStateTransitionTests
{
    [Theory]
    [InlineData("microphone", 0)]
    [InlineData("webcam", 1)]
    [InlineData("graphicsCaptureProgrammatic", 2)]
    [InlineData("graphicsCaptureWithoutBorder", 3)]
    [InlineData("location", 4)]
    public void StoppedUsageClearsOnNextPollEvenWithoutARegistryNotification(string capability, int index)
    {
        bool capturing = true;
        string consumer = Environment.ProcessPath!.Replace('\\', '#');
        var cache = new PrivacyIndicatorService.CapabilityUsageCache(name =>
            name == capability && capturing
                ? new[] { new CapabilityUsage(consumer, "Recorder", 10) }
                : Array.Empty<CapabilityUsage>());
        using var running = new PrivacyIndicatorService.ConsumerProcessProbe();

        Assert.Single(PrivacyIndicatorService.GetRelevantConsumerUsages(
            cache.GetSnapshot(registryChanged: false, now: 0)[index], running.IsRunning));

        // Stopping capture does not necessarily terminate its application.
        capturing = false;
        Assert.True(running.IsRunning(consumer));
        Assert.Empty(PrivacyIndicatorService.GetRelevantConsumerUsages(
            cache.GetSnapshot(registryChanged: false, now: 1_000)[index], running.IsRunning));

        capturing = true;
        Assert.Single(cache.GetSnapshot(registryChanged: true, now: 2_000)[index]);
    }

    [Fact]
    public void IdleUsageIsCachedUntilNotificationOrFallbackScan()
    {
        int scans = 0;
        var cache = new PrivacyIndicatorService.CapabilityUsageCache(_ =>
        {
            scans++;
            return Array.Empty<CapabilityUsage>();
        });

        var idle = cache.GetSnapshot(registryChanged: false, now: 0);
        Assert.Equal(5, scans);
        Assert.Same(idle, cache.GetSnapshot(registryChanged: false, now: 1_000));
        Assert.Equal(5, scans);
        Assert.NotSame(idle, cache.GetSnapshot(registryChanged: true, now: 2_000));
        Assert.Equal(10, scans);
        cache.GetSnapshot(registryChanged: false, now: 32_000);
        Assert.Equal(15, scans);
    }

    [Fact]
    public void ActiveUsageRefreshesOnlyItsCapabilityWithoutDelayingTheFullScan()
    {
        var scans = new Dictionary<string, int>();
        bool cameraStarted = false;
        var cache = new PrivacyIndicatorService.CapabilityUsageCache(name =>
        {
            scans[name] = scans.GetValueOrDefault(name) + 1;
            return name == "microphone" || (name == "webcam" && cameraStarted)
                ? new[] { new CapabilityUsage("Recorder", "Recorder", 10) }
                : Array.Empty<CapabilityUsage>();
        });

        cache.GetSnapshot(registryChanged: false, now: 0);
        cache.GetSnapshot(registryChanged: false, now: 1_000);
        Assert.Equal(2, scans["microphone"]);
        Assert.Equal(1, scans["webcam"]);
        Assert.Equal(1, scans["graphicsCaptureProgrammatic"]);
        Assert.Equal(1, scans["graphicsCaptureWithoutBorder"]);
        Assert.Equal(1, scans["location"]);

        // Repeated microphone polls must not postpone discovery of a camera
        // start whose notification was missed while its snapshot was idle.
        cameraStarted = true;
        Assert.Empty(cache.GetSnapshot(registryChanged: false, now: 29_000)[1]);
        Assert.Single(cache.GetSnapshot(registryChanged: false, now: 30_000)[1]);
        Assert.Equal(2, scans["webcam"]);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void PrivacyChangesPublishWithoutWaitingForAMicrophoneProbe(bool camera, bool screen, bool location)
    {
        using var service = new PrivacyIndicatorService();
        var changes = new List<PrivacyIndicatorState>();
        service.StateChanged += (_, state) => changes.Add(state);

        ApplyScan(service, camera, screen, location, hasMicrophoneCandidate: true);
        Assert.True(service.CurrentState.AnyInUse);
        Assert.Equal(camera, service.CurrentState.CameraInUse);
        Assert.Equal(screen, service.CurrentState.ScreenRecordingActive);
        Assert.Equal(location, service.CurrentState.LocationInUse);
        Assert.Single(changes);

        // The microphone candidate remains, but its worker has not published.
        ApplyScan(service, false, false, false, hasMicrophoneCandidate: true);
        Assert.Equal(PrivacyIndicatorState.Empty, service.CurrentState);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void CameraStopPreservesAnActiveMicrophoneUntilItsOwnEvidenceClears()
    {
        using var service = new PrivacyIndicatorService();
        ApplyScan(service, true, false, false, hasMicrophoneCandidate: true);
        typeof(PrivacyIndicatorService).GetMethod("PublishState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { true, new MicrophoneFlowEvidence(true, 0.5f) });

        ApplyScan(service, false, false, false, hasMicrophoneCandidate: true);
        Assert.False(service.CurrentState.CameraInUse);
        Assert.Empty(service.CurrentState.CameraConsumers);
        Assert.True(service.CurrentState.MicrophoneInUse);
        Assert.Single(service.CurrentState.MicrophoneConsumers);

        ApplyScan(service, false, false, false, hasMicrophoneCandidate: false);
        Assert.Equal(PrivacyIndicatorState.Empty, service.CurrentState);
    }

    private static void ApplyScan(PrivacyIndicatorService service, bool camera, bool screen,
        bool location, bool hasMicrophoneCandidate)
    {
        var candidates = hasMicrophoneCandidate
            ? new[] { new CapabilityUsage("MicrophoneApp", "MicrophoneApp", 10) }
            : Array.Empty<CapabilityUsage>();
        var snapshotType = typeof(PrivacyIndicatorService).GetNestedType("PrivacyScanResult", BindingFlags.NonPublic)!;
        var snapshot = Activator.CreateInstance(snapshotType, new object[]
        {
            candidates,
            hasMicrophoneCandidate ? new[] { "MicrophoneApp" } : Array.Empty<string>(),
            camera ? new[] { "CameraApp" } : Array.Empty<string>(),
            camera,
            screen,
            location ? new[] { "LocationApp" } : Array.Empty<string>(),
            DateTime.UtcNow
        });
        typeof(PrivacyIndicatorService).GetMethod("ApplyScanResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new[] { snapshot });
    }
}
