using VNotch.Models;

namespace VNotch.Services;

public sealed partial class MediaDetectionService
{
    private readonly object _spotifyArtistLookupGate = new();
    private SpotifyArtistLookup? _spotifyArtistLookup;
    internal TimeSpan SpotifyArtistLookupTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private sealed class SpotifyArtistLookup(string identity, string track, string artist, string album,
        TimeSpan duration, string sourceApp, string session, int generation, CancellationToken caller)
    {
        internal readonly string Identity = identity, Track = track, Artist = artist, Album = album;
        internal readonly string SourceApp = sourceApp, Session = session;
        internal readonly TimeSpan Duration = duration;
        internal readonly int Generation = generation;
        internal readonly CancellationToken Caller = caller;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
        internal Task Worker = Task.CompletedTask;
        internal bool Superseded;
    }

    internal Task SpotifyArtistLookupCompletion
    {
        get { lock (_spotifyArtistLookupGate) return _spotifyArtistLookup?.Worker ?? Task.CompletedTask; }
    }

    private void StartSpotifyArtistLookup(MediaInfo info, string track, string artist, string album, TimeSpan duration, CancellationToken ct)
    {
        string identity = $"{info.SourceAppId}\0{info.SessionInstanceKey}\0{track}\0{artist}\0{album}";
        lock (_spotifyArtistLookupGate)
        {
            if (_spotifyArtistLookup is { } current && current.Identity == identity &&
                !current.Caller.IsCancellationRequested && !current.Worker.IsCompleted)
                return;
            CancelSpotifyArtistLookup();
            var lookup = new SpotifyArtistLookup(identity, track, artist, album, duration,
                info.SourceAppId, info.SessionInstanceKey, _lifecycleGeneration, ct);
            _spotifyArtistLookup = lookup;
            lookup.Cancellation.CancelAfter(SpotifyArtistLookupTimeout);
            lookup.Worker = CompleteSpotifyArtistLookupAsync(lookup);
        }
    }

    private Task CancelSpotifyArtistLookup()
    {
        lock (_spotifyArtistLookupGate)
        {
            var lookup = _spotifyArtistLookup;
            _spotifyArtistLookup = null;
            if (lookup == null) return Task.CompletedTask;
            lookup.Superseded = true;
            try { lookup.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* A completed lookup already released its token. */ }
            return lookup.Worker;
        }
    }

    private async Task CompleteSpotifyArtistLookupAsync(SpotifyArtistLookup lookup)
    {
        string? artists = null;
        try
        {
            var request = _metadataLookup.TryGetSpotifyFullArtistsAsync(lookup.Track, lookup.Artist,
                lookup.Duration, lookup.Album, lookup.Cancellation.Token);
            // A provider that ignores cancellation can finish after our bounded
            // waiter exits. Observe its fault without retaining the service.
            _ = request.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            artists = await request.WaitAsync(lookup.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Timeout falls back once; supersession/shutdown is discarded below. */ }
        catch (Exception ex) { RuntimeLog.Debug("MEDIA-ARTISTS", $"Artist lookup failed: {ex.Message}"); }
        finally
        {
            lock (_spotifyArtistLookupGate)
            {
                if (ReferenceEquals(_spotifyArtistLookup, lookup) && !lookup.Superseded &&
                    !lookup.Caller.IsCancellationRequested && !_disposed &&
                    lookup.Generation == _lifecycleGeneration &&
                    _state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
                    AcceptsMediaUpdate(new MediaInfo { SourceAppId = lookup.SourceApp, SessionInstanceKey = lookup.Session }))
                {
                    string resolved = string.IsNullOrWhiteSpace(artists) ? lookup.Artist : artists.Trim();
                    StoreSpotifyArtistCache(lookup.Track, lookup.Artist, lookup.Album, lookup.Duration, resolved);
                    _changes.Enqueue(ChangeTypes.ForceRefresh);
                }
            }
            lookup.Cancellation.Dispose();
        }
    }
}
