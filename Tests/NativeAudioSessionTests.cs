using VNotch.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeAudioSessionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ControlledSessionVolumeRoundTripsOnEachActiveOutput()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        output.WriteLine($"active outputs={devices.Count}");
        foreach (var device in devices)
        {
            using (device)
            using (var playback = new WasapiOut(device, AudioClientShareMode.Shared, false, 100))
            {
                // Digital silence creates a real session without playing a sound to the user.
                playback.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
                playback.Play();
                await Task.Delay(150);
                using var sessions = WindowsMediaAudioSessions.ReadAllEndpoints();
                var controlled = Assert.Single(sessions.Sessions.Where(s => s.GetProcessID == (uint)Environment.ProcessId && s.State == 1));
                using var lease = controlled.SimpleAudioVolume;
                float originalVolume = lease.Volume;
                bool originalMute = lease.Mute;
                using var service = new MediaSessionVolumeService();
                string source = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
                try
                {
                    Assert.True(lease.IsActive);
                    Assert.True(service.TrySetVolume(source, .42f));
                    Assert.Equal(.42f, lease.Volume, 4);
                    Assert.True(service.TryGetVolume(source, out float actual, out _));
                    Assert.Equal(.42f, actual, 4);
                    Assert.True(service.TrySetVolume(source, .58f));
                    Assert.Equal(.58f, lease.Volume, 4);
                    output.WriteLine($"{device.FriendlyName}: volume .42 -> .58 confirmed");
                }
                finally
                {
                    lease.Volume = originalVolume;
                    lease.Mute = originalMute;
                    playback.Stop();
                }
            }
        }
    }

    // Read-only: sessions are listed and their meters and volumes read, never modified.
    [Fact]
    public void DefaultEndpointSessionsAreEnumeratedAndReadable()
    {
        MediaAudioSessions sessions;
        try { sessions = WindowsMediaAudioSessions.Read(); }
        catch (System.Runtime.InteropServices.COMException) { return; } // No render endpoint on this machine.

        using (sessions)
        {
            output.WriteLine("sessions=" + sessions.Sessions.Count);
            foreach (var session in sessions.Sessions)
            {
                Assert.NotNull(session.GetSessionIdentifier);
                Assert.InRange(session.ReadPeak(), 0f, 1f);
                using var volume = session.SimpleAudioVolume;
                Assert.InRange(volume.Volume, 0f, 1f);
                _ = volume.Mute;
                output.WriteLine($"pid={session.GetProcessID} system={session.IsSystemSoundsSession} id={session.GetSessionIdentifier}");
            }
        }
    }

    [Fact]
    public void VolumeLeaseOutlivesItsSnapshot()
    {
        MediaAudioSessions sessions;
        try { sessions = WindowsMediaAudioSessions.Read(); }
        catch (System.Runtime.InteropServices.COMException) { return; }

        IMediaAudioVolume? lease = null;
        using (sessions)
        {
            if (sessions.Sessions.Count == 0) return;
            lease = sessions.Sessions[0].SimpleAudioVolume;
        }

        // The service caches a lease after the snapshot is closed; it must keep working.
        using (lease) Assert.InRange(lease.Volume, 0f, 1f);
    }
}
