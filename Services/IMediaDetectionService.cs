using VNotch.Models;

namespace VNotch.Services;

public interface IMediaDetectionService : IDisposable
{

    event EventHandler<MediaInfo>? MediaChanged;

    void Start();

    void Stop();

    Task PlayPauseAsync();

    Task NextTrackAsync();

    Task PreviousTrackAsync();

    Task SeekAsync(TimeSpan position);

    Task SeekRelativeAsync(double seconds);

    Task SeekToAbsoluteAsync(TimeSpan position);

    bool TryGetCurrentSessionVolume(out float volume, out bool isMuted);

    bool TrySetCurrentSessionVolume(float volume);

    bool TryToggleCurrentSessionMute();

    void InvalidateVolumeSessionCache();
}
