using NAudio.CoreAudioApi;

namespace VNotch.Services;

internal interface IMediaAudioVolume : IDisposable
{
    float Volume { get; set; }
    bool Mute { get; set; }
}

internal sealed record MediaAudioSessionSnapshot(
    uint GetProcessID, string? DisplayName, string? GetSessionIdentifier,
    string? GetSessionInstanceIdentifier, bool IsSystemSoundsSession,
    Func<float> ReadPeak, Func<IMediaAudioVolume> OpenVolume)
{
    internal IMediaAudioVolume SimpleAudioVolume => OpenVolume();
}

internal sealed class MediaAudioSessions(IReadOnlyList<MediaAudioSessionSnapshot> sessions, Action? dispose = null) : IDisposable
{
    internal IReadOnlyList<MediaAudioSessionSnapshot> Sessions { get; } = sessions;
    public void Dispose() => dispose?.Invoke();
}

internal static class WindowsMediaAudioSessions
{
    internal static MediaAudioSessions Read()
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        var controls = new List<AudioSessionControl>();
        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var snapshots = new List<MediaAudioSessionSnapshot>();
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var control = sessions[i];
                if (control == null) continue;
                controls.Add(control);
                snapshots.Add(new(control.GetProcessID, control.DisplayName, control.GetSessionIdentifier,
                    control.GetSessionInstanceIdentifier, control.IsSystemSoundsSession,
                    () => control.AudioMeterInformation.MasterPeakValue,
                    () => new VolumeLease(control.SimpleAudioVolume)));
            }
            return new MediaAudioSessions(snapshots, () =>
            {
                foreach (var control in controls) control.Dispose();
                device.Dispose();
                enumerator.Dispose();
            });
        }
        catch
        {
            foreach (var control in controls) control.Dispose();
            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    private sealed class VolumeLease(SimpleAudioVolume volume) : IMediaAudioVolume
    {
        public float Volume { get => volume.Volume; set => volume.Volume = value; }
        public bool Mute { get => volume.Mute; set => volume.Mute = value; }
        public void Dispose() => volume.Dispose();
    }
}
