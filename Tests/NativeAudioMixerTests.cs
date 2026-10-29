using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeAudioMixerTests(ITestOutputHelper output)
{
    // Read-only: devices, sessions and the capture volume are listed and read, never changed.
    [Fact]
    public void DevicesSessionsAndCaptureVolumeAreReadFromNativeAudio()
    {
        using var mixer = new AudioMixerService();

        var outputs = mixer.GetOutputDevices();
        var inputs = mixer.GetInputDevices();
        output.WriteLine($"outputs={outputs.Count} inputs={inputs.Count}");
        foreach (var device in outputs.Concat(inputs))
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.FriendlyName));
        }

        Assert.True(outputs.Count(d => d.IsDefault) <= 1);
        Assert.True(inputs.Count(d => d.IsDefault) <= 1);
        Assert.Equal(outputs.Count, outputs.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var sessions = mixer.GetSessions(includeIcons: false);
        output.WriteLine("sessions=" + string.Join(", ", sessions.Select(s => $"{s.DisplayName}(pid {s.ProcessId})")));
        Assert.Equal(sessions.Count, sessions.Select(s => s.ProcessId).Distinct().Count());
        Assert.All(sessions, s =>
        {
            Assert.InRange(s.Volume, 0f, 1f);
            Assert.False(string.IsNullOrWhiteSpace(s.DisplayName));
        });

        Assert.InRange(mixer.GetCaptureVolume(), 0f, 1f);
    }

    [Fact]
    public void ReleaseAndDisposeAreRepeatable()
    {
        var mixer = new AudioMixerService();
        mixer.ReleaseSessionCache();
        mixer.ReleaseSessionCache();
        mixer.Dispose();
        Assert.Null(Record.Exception(mixer.Dispose));
    }

    [Fact]
    public void UnknownProcessVolumeChangeFailsWithoutSideEffects()
    {
        using var mixer = new AudioMixerService();
        // No audio session belongs to this process id, so nothing is modified.
        Assert.False(mixer.SetSessionVolume(uint.MaxValue - 7, 0.5f));
        Assert.False(mixer.ToggleSessionMute(uint.MaxValue - 7));
    }
}
