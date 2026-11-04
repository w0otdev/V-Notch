using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace VNotch.Services;

// Spotify exposes the complete current artist list in its document title. Read
// that local snapshot before using catalog searches, which can take seconds.
internal sealed partial class SpotifyLocalMetadataReader
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_read_track_link(nint window, string documentName, string title,
        Span<char> output, int capacity, out int written);

    internal sealed record Track(string Title, string Artists, string? TrackId = null);
    private static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(180);
    private readonly object _gate = new();
    private readonly Func<Track?> _read;
    private Task<Track?>? _pending;
    private Track? _cached;
    private AutomationElement? _document;
    private nint _window;
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
            await Task.Delay(15, ct).ConfigureAwait(false);
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

    private Track? ReadDocumentTrack(AutomationElement document, nint window)
    {
        string documentName = document.Current.Name;
        var track = ParseDocumentTitle(documentName);
        if (track == null || !_includeTrackId) return track;
        // Read only links whose accessible name is the current song title. Never
        // use the first track link in a playlist or invoke/copy anything in Spotify.
        Span<char> url = stackalloc char[2048];
        string? id = vn_spotify_read_track_link(window, documentName, track.Title, url, url.Length, out int written) == 0 &&
                     written > 0 && written <= url.Length
            ? LyricsService.NormalizeSpotifyTrackId(new string(url[..written])) : null;
        // Discard a snapshot if playback changed while the accessibility tree was read.
        return ParseDocumentTitle(document.Current.Name) == track ? track with { TrackId = id } : null;
    }

    private Track? ReadCurrentTrack()
    {
        if (_document != null)
        {
            try
            {
                var current = ReadDocumentTrack(_document, _window);
                if (current != null) return current;
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
            _document = null;
        }

        var processes = Process.GetProcessesByName("Spotify");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    nint window = process.MainWindowHandle;
                    if (window == 0) continue;
                    var root = AutomationElement.FromHandle(window);
                    var document = root?.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    var track = document == null ? null : ReadDocumentTrack(document, window);
                    if (track == null) continue;
                    _document = document;
                    _window = window;
                    return track;
                }
                catch (ElementNotAvailableException) { continue; }
                catch (InvalidOperationException) { continue; }
                catch (Win32Exception) { continue; }
                catch (COMException) { continue; }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return null;
    }
}
