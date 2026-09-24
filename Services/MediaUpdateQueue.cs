using VNotch.Models;

namespace VNotch.Services;

// Retain metadata and artwork separately: artwork-only events must not displace
// playback state. Only one dispatcher callback is outstanding per consumer.
internal sealed class MediaUpdateQueue(Action<Action> schedule, Action<MediaInfo> apply) : IDisposable
{
    private readonly object _gate = new();
    private MediaInfo? _metadata;
    private MediaInfo? _artwork;
    private bool _scheduled;
    private bool _disposed;

    public void Enqueue(MediaInfo info)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (info.IsThumbnailOnlyUpdate) _artwork = info;
            else
            {
                _metadata = info;
                if (_artwork != null && (!SameTrack(_artwork, info) || info.Thumbnail != null))
                    _artwork = null;
            }
            if (_scheduled) return;
            _scheduled = true;
        }
        Schedule();
    }

    internal static bool SameTrack(MediaInfo left, MediaInfo right) =>
        string.Equals(left.CurrentTrack, right.CurrentTrack, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.CurrentArtist, right.CurrentArtist, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.MediaSource, right.MediaSource, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.SessionInstanceKey, right.SessionInstanceKey, StringComparison.Ordinal);

    private void Schedule()
    {
        try { schedule(Drain); }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Drain()
    {
        MediaInfo? metadata;
        MediaInfo? artwork;
        lock (_gate)
        {
            if (_disposed) return;
            metadata = _metadata;
            artwork = _artwork;
            _metadata = _artwork = null;
        }
        try
        {
            if (metadata != null) apply(metadata);
            if (artwork != null && (metadata == null || SameTrack(metadata, artwork))) apply(artwork);
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                again = !_disposed && (_metadata != null || _artwork != null);
                if (!again) _scheduled = false;
            }
            if (again) Schedule();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _metadata = _artwork = null;
        }
    }
}
