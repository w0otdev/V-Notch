using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class NativeAudioSessionTests(ITestOutputHelper output)
{
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
