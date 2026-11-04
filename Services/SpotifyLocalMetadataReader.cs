using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

// Spotify exposes the complete current artist list in its document title. Read
// that local snapshot before using catalog searches, which can take seconds.
internal sealed partial class SpotifyLocalMetadataReader
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_read_local_track(
        byte includeTrackId,
        Span<char> outTitle, int titleCap, out int outTitleLen,
        Span<char> outArtists, int artistsCap, out int outArtistsLen,
        Span<char> outTrackUrl, int urlCap, out int outUrlLen);

    internal sealed record Track(string Title, string Artists, string? TrackId = null);
    private static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(180);
    private readonly object _gate = new();
    private readonly Func<Track?> _read;
    private Task<Track?>? _pending;
    private Track? _cached;
    private readonly bool _includeTrackId;

    internal SpotifyLocalMetadataReader(bool includeTrackId = false)
    {
        _includeTrackId = includeTrackId;
        _read = ReadCurrentTrack;
    }
    internal SpotifyLocalMetadataReader(Func<Track?> read) => _read = read;

    internal void WarmUp() => StartRead();

    internal async Task<string?> TryGetArtistsAsync(string title, string primaryArtist, CancellationToken ct)
        => (await TryGetTrackAsync(title, primaryArtist, ct).ConfigureAwait(false))?.Artists;

    internal async Task<string?> TryGetTrackIdAsync(string title, string primaryArtist, CancellationToken ct)
        => (await TryGetTrackAsync(title, primaryArtist, ct, useCached: false).ConfigureAwait(false))?.TrackId;

    private async Task<Track?> TryGetTrackAsync(string title, string primaryArtist, CancellationToken ct, bool useCached = true)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (useCached && Matches(_cached, title, primaryArtist)) return _cached;
        }

        long started = Stopwatch.GetTimestamp();
        do
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan remaining = ReadBudget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;
            Track? snapshot;
            try
            {
                snapshot = await StartRead().WaitAsync(remaining, ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { return null; }
            if (Matches(snapshot, title, primaryArtist)) return snapshot;
            if (snapshot == null) return null;

            // SMTC and the Spotify title can notify in either order. Give the
            // local title a few frames to catch up before falling back online.
            await Task.Delay(8, ct).ConfigureAwait(false);
        } while (Stopwatch.GetElapsedTime(started) < ReadBudget);
        return null;
    }

    private Task<Track?> StartRead()
    {
        lock (_gate)
        {
            // A stuck accessibility provider must never accumulate workers.
            if (_pending is { IsCompleted: false }) return _pending;
            return _pending = Task.Run(() =>
            {
                Track? snapshot;
                try { snapshot = _read(); }
                catch { snapshot = null; }
                lock (_gate) { if (snapshot != null) _cached = snapshot; }
                return snapshot;
            });
        }
    }

    internal static Track? ParseDocumentTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        int separator = value.LastIndexOf(" • ", StringComparison.Ordinal);
        if (separator <= 0) return null;
        string title = value[..separator].Trim();
        string artists = value[(separator + 3)..].Trim();
        return title.Length == 0 || artists.Length == 0 ? null : new(title, artists);
    }

    internal static bool Matches(Track? track, string title, string primaryArtist)
        => track != null && string.Equals(track.Title, title.Trim(), StringComparison.OrdinalIgnoreCase) &&
           (string.Equals(track.Artists, primaryArtist.Trim(), StringComparison.OrdinalIgnoreCase) ||
            track.Artists.StartsWith(primaryArtist.Trim() + ", ", StringComparison.OrdinalIgnoreCase) ||
            primaryArtist.Trim().StartsWith(track.Artists + ", ", StringComparison.OrdinalIgnoreCase));

    private Track? ReadCurrentTrack()
    {
        Span<char> titleBuf = stackalloc char[512];
        Span<char> artistsBuf = stackalloc char[512];
        Span<char> urlBuf = stackalloc char[2048];

        int rc = vn_spotify_read_local_track(
            _includeTrackId ? (byte)1 : (byte)0,
            titleBuf, titleBuf.Length, out int titleLen,
            artistsBuf, artistsBuf.Length, out int artistsLen,
            urlBuf, urlBuf.Length, out int urlLen);

        if (rc != 0 || titleLen <= 0 || artistsLen <= 0)
            return null;

        string title = new(titleBuf[..Math.Min(titleLen, titleBuf.Length)]);
        string artists = new(artistsBuf[..Math.Min(artistsLen, artistsBuf.Length)]);
        string? trackId = _includeTrackId && urlLen > 0 && urlLen <= urlBuf.Length
            ? LyricsService.NormalizeSpotifyTrackId(new string(urlBuf[..urlLen]))
            : null;
        return new(title, artists, trackId);
    }
}
