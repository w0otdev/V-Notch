using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

// Retain metadata and artwork separately: artwork-only events must not displace
// playback state. Only one dispatcher callback is outstanding per consumer.
internal sealed partial class MediaUpdateQueue(Action<Action> schedule, Action<MediaInfo> apply) : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_update_queue_same_track(
        ReadOnlySpan<char> leftTrack, int leftTrackLen,
        ReadOnlySpan<char> rightTrack, int rightTrackLen,
        ReadOnlySpan<char> leftArtist, int leftArtistLen,
        ReadOnlySpan<char> rightArtist, int rightArtistLen,
        ReadOnlySpan<char> leftSource, int leftSourceLen,
        ReadOnlySpan<char> rightSource, int rightSourceLen,
        ReadOnlySpan<char> leftSessionKey, int leftSessionLen,
        ReadOnlySpan<char> rightSessionKey, int rightSessionLen,
        out byte outSame);

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

    internal static bool SameTrack(MediaInfo left, MediaInfo right)
    {
        ReadOnlySpan<char> lt = left.CurrentTrack is not null ? left.CurrentTrack.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> rt = right.CurrentTrack is not null ? right.CurrentTrack.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> la = left.CurrentArtist is not null ? left.CurrentArtist.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> ra = right.CurrentArtist is not null ? right.CurrentArtist.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> ls = left.MediaSource is not null ? left.MediaSource.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> rs = right.MediaSource is not null ? right.MediaSource.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> lk = left.SessionInstanceKey is not null ? left.SessionInstanceKey.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> rk = right.SessionInstanceKey is not null ? right.SessionInstanceKey.AsSpan() : ReadOnlySpan<char>.Empty;

        if (vn_media_update_queue_same_track(
                lt, lt.Length, rt, rt.Length,
                la, la.Length, ra, ra.Length,
                ls, ls.Length, rs, rs.Length,
                lk, lk.Length, rk, rk.Length,
                out byte outSame) == 0)
        {
            return outSame != 0;
        }

        return string.Equals(left.CurrentTrack, right.CurrentTrack, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.CurrentArtist, right.CurrentArtist, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.MediaSource, right.MediaSource, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.SessionInstanceKey, right.SessionInstanceKey, StringComparison.Ordinal);
    }

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
