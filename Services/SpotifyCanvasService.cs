using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using static VNotch.Services.SpotifyTrackMatcher;

namespace VNotch.Services;

public sealed class SpotifyCanvasService : IDisposable
{
    private const string LogCategory = "SPOTIFY-CANVAS";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private const int MaxCacheEntries = 128;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SpotifyTokenProvider _tokens;
    private readonly SpotifyQueryMetadataProvider _metadata;
    private readonly SpotifyCatalogClient _catalog;
    private readonly MusixmatchTrackResolver _musixmatch;
    private readonly SpotifyCanvasClient _canvas;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    public SpotifyCanvasService()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal SpotifyCanvasService(HttpClient http)
        : this(http, ownsHttpClient: false)
    {
    }

    private SpotifyCanvasService(HttpClient http, bool ownsHttpClient)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttpClient = ownsHttpClient;
        _tokens = new SpotifyTokenProvider(http);
        _metadata = new SpotifyQueryMetadataProvider(http);
        _catalog = new SpotifyCatalogClient(http, _metadata);
        _musixmatch = new MusixmatchTrackResolver(http);
        _canvas = new SpotifyCanvasClient(http, _metadata);
    }

    public async Task<Uri?> FetchCanvasAsync(
        string trackName,
        string artistName,
        TimeSpan duration,
        string? spotifySpDc,
        CancellationToken cancellationToken = default)
    {
        string? sessionCookie = NormalizeSessionCookie(spotifySpDc);
        if (string.IsNullOrWhiteSpace(trackName) || sessionCookie == null || cancellationToken.IsCancellationRequested)
            return null;

        string cacheKey = BuildCacheKey(trackName, artistName, duration);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                return cached.CanvasUri;

            ((ICollection<KeyValuePair<string, CacheEntry>>)_cache).Remove(new(cacheKey, cached));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            RuntimeLog.Debug(LogCategory, "Starting Spotify Canvas lookup");
            string? accessToken = await _tokens.GetAccessTokenAsync(sessionCookie, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken))
            {
                RuntimeLog.Debug(LogCategory, "Spotify access token was unavailable");
                return null;
            }

            RuntimeLog.Debug(LogCategory, "Spotify access token is ready");

            string? trackId = await ResolveTrackIdAsync(
                trackName,
                artistName,
                duration,
                accessToken,
                timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(trackId))
                return null;

            Uri? canvasUri = await _canvas.FetchAsync(trackId, accessToken, timeoutCts.Token).ConfigureAwait(false);
            if (canvasUri != null)
                CacheCanvas(cacheKey, canvasUri);

            return canvasUri;
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Debug(LogCategory, () =>
                cancellationToken.IsCancellationRequested
                    ? "Canvas lookup was superseded by a media change"
                    : $"Canvas lookup timed out after {RequestTimeout.TotalSeconds:0} seconds");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"Canvas lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> ValidateSessionAsync(
        string? spotifySpDc,
        CancellationToken cancellationToken = default)
    {
        string? sessionCookie = NormalizeSessionCookie(spotifySpDc);
        if (sessionCookie == null)
            return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            return !string.IsNullOrEmpty(
                await _tokens.GetAccessTokenAsync(sessionCookie, timeoutCts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug(LogCategory, () => $"Spotify session validation failed: {ex.Message}");
            return false;
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock) _cache.Clear();
        _tokens.ClearCache();
    }

    private async Task<string?> ResolveTrackIdAsync(
        string trackName,
        string artistName,
        TimeSpan duration,
        string accessToken,
        CancellationToken token)
    {
        string? trackId = await _catalog.ResolveTrackIdAsync(
            trackName,
            artistName,
            accessToken,
            token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(trackId))
            return trackId;

        RuntimeLog.Debug(LogCategory, "Spotify catalog lookup did not resolve the track; trying Musixmatch metadata");
        return await _musixmatch.ResolveTrackIdAsync(
            trackName,
            artistName,
            duration,
            token).ConfigureAwait(false);
    }

    private static string BuildCacheKey(string track, string artist, TimeSpan duration) =>
        $"{NormalizeForMatch(track)}|{NormalizeForMatch(artist)}|{Math.Round(duration.TotalSeconds)}";

    private static string? NormalizeSessionCookie(string? value)
    {
        string cookie = value?.Trim() ?? "";
        if (cookie.Length is 0 or > 4096 || cookie.IndexOfAny([';', '\r', '\n']) >= 0)
            return null;
        return cookie;
    }

    private void CacheCanvas(string key, Uri uri)
    {
        lock (_cacheLock)
        {
            _cache[key] = new CacheEntry(uri, DateTimeOffset.UtcNow + CacheLifetime);
            TrimCacheIfNeeded();
        }
    }

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count <= MaxCacheEntries) return;

        var now = DateTimeOffset.UtcNow;
        KeyValuePair<string, CacheEntry>? oldest = null;
        foreach (var entry in _cache)
        {
            if (entry.Value.ExpiresAtUtc <= now)
                ((ICollection<KeyValuePair<string, CacheEntry>>)_cache).Remove(entry);
            else if (oldest == null || entry.Value.ExpiresAtUtc < oldest.Value.Value.ExpiresAtUtc)
                oldest = entry;
        }
        // Writers hold _cacheLock, so one linear pass can evict the single overflow entry.
        if (_cache.Count > MaxCacheEntries && oldest is { } victim)
            ((ICollection<KeyValuePair<string, CacheEntry>>)_cache).Remove(victim);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };
        var privacyHandler = NetworkPrivacy.Handler(NetworkFeature.Canvas, handler);
        var client = new HttpClient(privacyHandler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.8 SpotifyCanvas");
        return client;
    }

    public void Dispose()
    {
        _tokens.Dispose();
        _musixmatch.Dispose();
        _metadata.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record CacheEntry(Uri CanvasUri, DateTimeOffset ExpiresAtUtc);
}
