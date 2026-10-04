using VNotch.Models;

namespace VNotch.Services;

public interface IMediaDetectionService : IDisposable
{
    IMediaArtworkService ArtworkService { get; }

    bool KeepPinnedOnTrackChange { get; set; }

    bool IsSessionPinned(string? sessionKey);

    bool ToggleSessionPin(MediaInfo info);

    Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default);

    event EventHandler<MediaInfo>? MediaChanged;

    bool AcceptsMediaUpdate(MediaInfo info) => true;

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
