using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyIndicatorServiceTests
{
    [Fact]
    public void RelevantConsumersKeepNewestUsageAndFilterBeforeProbingProcesses()
    {
        var usages = new[]
        {
            new CapabilityUsage("Recorder", "old", 10),
            new CapabilityUsage("RECORDER", "new", 20),
            new CapabilityUsage("stopped", "stopped", 30),
            new CapabilityUsage("service", "service", 40)
        };
        var probed = new List<string>();
        var result = PrivacyIndicatorService.GetRelevantConsumerUsages(usages,
            name => { probed.Add(name); return name != "stopped"; },
            usage => usage.RawName != "service");

        Assert.Equal("new", Assert.Single(result).DisplayName);
        Assert.DoesNotContain("service", probed);
    }

    [Fact]
    public void ProcessEvidenceMatchesTheFullExecutablePathWithLimitedQueryAccess()
    {
        string executable = Environment.ProcessPath!;
        uint pid = (uint)Environment.ProcessId;
        Assert.True(PrivacyIndicatorService.ConsumerProcessProbe.IsDesktopExecutableProcess(executable, pid));
        Assert.False(PrivacyIndicatorService.ConsumerProcessProbe.IsDesktopExecutableProcess(executable + ".other", pid));
        Assert.False(PrivacyIndicatorService.ConsumerProcessProbe.IsDesktopExecutableProcess(executable, 0));
        var probe = new PrivacyIndicatorService.ConsumerProcessProbe();
        Assert.True(probe.IsRunning(executable.Replace('\\', '#')));
        Assert.False(probe.IsRunning(@"C:#missing-directory#" + System.IO.Path.GetFileName(executable)));
    }

    [Fact]
    public void DotColor_AllActivityCombinationsFollowPriority()
    {
        for (int flags = 0; flags < 16; flags++)
        {
            var state = new PrivacyIndicatorState(
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                Array.Empty<string>(), Array.Empty<string>())
            {
                LocationInUse = (flags & 8) != 0
            };
            string expected = state.ScreenRecordingActive ? "#FFFF3B30"
                : state.MicrophoneInUse ? "#FFFF9500"
                : state.CameraInUse ? "#FF30D158"
                : state.LocationInUse ? "#FF00AEEF" : "#00FFFFFF";
            Assert.Equal(expected, MainWindow.GetPrivacyDotColor(state).ToString());
            Assert.Equal(flags != 0, state.AnyInUse);
        }
    }

    [Fact]
    public void LocationChangesParticipateInStateEquality()
    {
        var active = PrivacyIndicatorState.Empty with
        {
            LocationInUse = true,
            LocationConsumers = new[] { "Maps.exe" }
        };
        Assert.NotEqual(PrivacyIndicatorState.Empty, active);
        Assert.NotEqual(active, active with { LocationInUse = false });
        Assert.NotEqual(active, active with { LocationConsumers = new[] { "Weather.exe" } });
        var equivalent = active with { LocationConsumers = new[] { "MAPS.EXE" } };
        Assert.Equal(active, equivalent);
        Assert.Equal(active.GetHashCode(), equivalent.GetHashCode());
    }

    [Theory]
    [InlineData(1L, 0L, true)]
    [InlineData(0L, 0L, false)]
    [InlineData(1L, 2L, false)]
    [InlineData(null, 0L, false)]
    [InlineData(1L, null, false)]
    public void ActiveUsage_RequiresAValidOpenInterval(long? start, long? stop, bool expected)
    {
        Assert.Equal(expected, PrivacyIndicatorService.IsActiveUsage(start, stop));
    }

    [Fact]
    public void ScreenRecording_RequiresSustainedCapture()
    {
        DateTime now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(PrivacyIndicatorService.HasMinimumActiveDuration(
            now.AddMilliseconds(-500).ToFileTimeUtc(), now, TimeSpan.FromSeconds(2)));
        Assert.True(PrivacyIndicatorService.HasMinimumActiveDuration(
            now.AddSeconds(-3).ToFileTimeUtc(), now, TimeSpan.FromSeconds(2)));
        Assert.False(PrivacyIndicatorService.HasMinimumActiveDuration(
            now.AddSeconds(1).ToFileTimeUtc(), now, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Microphone_IgnoresBackgroundServicesButKeepsUserApps()
    {
        var noRegisteredServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(
            @"C:#ProgramData#Maono#Ai service#MaonoAiServices.exe", noRegisteredServices));
        Assert.True(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(
            "Elgato.WaveLink_g54w8ztgkx496", noRegisteredServices));
        Assert.False(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(
            @"D:#obs-studio#bin#64bit#obs64.exe", noRegisteredServices));
    }

    [Fact]
    public void MicrophoneActivity_RequiresMatchedSessionAndRealSignal()
    {
        DateTime now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var gate = new PrivacyIndicatorService.MicrophoneActivityGate(
            PrivacyIndicatorService.MicrophoneSignalThreshold,
            PrivacyIndicatorService.MicrophoneSignalHoldDuration);

        Assert.False(gate.Evaluate(
            hasCandidate: true,
            hasActiveSession: false,
            peakLevel: 0.5f,
            now));
        Assert.False(gate.Evaluate(
            hasCandidate: true,
            hasActiveSession: true,
            peakLevel: PrivacyIndicatorService.MicrophoneSignalThreshold / 2,
            now));
        Assert.True(gate.Evaluate(
            hasCandidate: true,
            hasActiveSession: true,
            peakLevel: PrivacyIndicatorService.MicrophoneSignalThreshold * 2,
            now));
    }

    [Fact]
    public void MicrophoneActivity_HoldsBetweenWordsButClearsOnSilenceOrClosedSession()
    {
        DateTime now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var gate = new PrivacyIndicatorService.MicrophoneActivityGate(
            PrivacyIndicatorService.MicrophoneSignalThreshold,
            PrivacyIndicatorService.MicrophoneSignalHoldDuration);

        Assert.True(gate.Evaluate(true, true, 0.5f, now));
        Assert.True(gate.Evaluate(
            true,
            true,
            0,
            now + PrivacyIndicatorService.MicrophoneSignalHoldDuration - TimeSpan.FromMilliseconds(1)));
        Assert.False(gate.Evaluate(
            true,
            true,
            0,
            now + PrivacyIndicatorService.MicrophoneSignalHoldDuration + TimeSpan.FromMilliseconds(1)));

        Assert.True(gate.Evaluate(true, true, 0.5f, now.AddSeconds(3)));
        Assert.False(gate.Evaluate(true, false, 0.5f, now.AddSeconds(3.1)));
    }

    [Fact]
    public void Microphone_IgnoresExecutablesRegisteredAsWindowsServices()
    {
        string raw = @"C:#Program Files#Vendor#AudioProcessor.exe";
        string path = PrivacyIndicatorService.TryDecodeDesktopConsumerPath(raw)!;
        var registeredServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };

        Assert.True(PrivacyIndicatorService.IsIgnoredMicrophoneConsumer(raw, registeredServices));
    }

    [Fact]
    public void DesktopConsumerPath_DecodesConsentStoreFormat()
    {
        string? decoded = PrivacyIndicatorService.TryDecodeDesktopConsumerPath(
            @"C:#Program Files#Recorder#recorder.exe");

        Assert.Equal(@"C:\Program Files\Recorder\recorder.exe", decoded);
        Assert.Null(PrivacyIndicatorService.TryDecodeDesktopConsumerPath(
            "Microsoft.WindowsCamera_8wekyb3d8bbwe"));
    }

    [Fact]
    public void Service_StartAndStop_TransitionsCleanly()
    {
        using var service = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(500));
        Assert.NotNull(service.CurrentState);

        // Start should launch background worker
        service.Start();

        // Idempotent start
        service.Start();

        // Stop should cancel background worker and stop flow timers
        service.Stop();

        // Idempotent stop
        service.Stop();

        // Restarting after stop should work cleanly
        service.Start();
        service.Stop();
    }

    [Fact]
    public void Service_Dispose_CleansUpProperly()
    {
        var service = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(500));
        service.Start();
        service.Dispose();

        // Double dispose should not throw
        service.Dispose();

        // Start after dispose should be a no-op
        service.Start();
    }
}
