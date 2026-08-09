using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services;

/// <summary>
/// Resolves the current Spotify track and retrieves its Canvas directly from
/// Spotify using the user's web session. Canvas is optional: every failure
/// returns null so the normal lyrics background remains untouched.
/// </summary>
internal sealed class SpotifyCanvasService : IDisposable
{
    private static readonly Uri SecretsUri = new(
        "https://raw.githubusercontent.com/xyloflake/spot-secrets-go/refs/heads/main/secrets/secretDict.json");
    private static readonly Uri ServerTimeUri = new("https://open.spotify.com/api/server-time");
    private static readonly Uri TokenUri = new("https://open.spotify.com/api/token");
    private static readonly Uri MusixmatchBaseUri = new("https://apic-desktop.musixmatch.com/");
    private static readonly Uri PathfinderUri = new("https://api-partner.spotify.com/pathfinder/v2/query");
    private static readonly Uri CanvasPathfinderUri = new("https://api-partner.spotify.com/pathfinder/v1/query");
    private static readonly Uri SpotifyWebUri = new("https://open.spotify.com/");
    private static readonly Uri LegacyCanvasUri = new("https://spclient.wg.spotify.com/canvaz-cache/v0/canvases");
    private static readonly Regex MobileWebPlayerScriptRegex = new(
        "https://open\\.spotifycdn\\.com/cdn/build/mobile-web-player/mobile-web-player\\.[a-f0-9]+\\.(?:js|mjs)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DesktopWebPlayerScriptRegex = new(
        "https://open\\.spotifycdn\\.com/cdn/build/web-player/web-player\\.[a-f0-9]+\\.(?:js|mjs)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FindTracksHashRegex = new(
        "[\\\"']findTracks[\\\"']\\s*,\\s*[\\\"']query[\\\"']\\s*,\\s*[\\\"'](?<hash>[a-f0-9]{64})[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CanvasHashRegex = new(
        "[\\\"']canvas[\\\"']\\s*,\\s*[\\\"']query[\\\"']\\s*,\\s*[\\\"'](?<hash>[a-f0-9]{64})[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan TotpSecretLifetime = TimeSpan.FromHours(1);
    private const string DefaultFindTracksHash = "903df2a65d8121e27d73a2be03c01e88ebe6021bb6d4eb82a389e35d87e51d27";
    private const string DefaultCanvasHash = "575138ab27cd5c1b3e54da54d0a7cc8d85485402de26340c2145f0f6bb5e7a9f";
    private const string DesktopWebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";
    private const string MobileWebUserAgent =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Mobile Safari/537.36";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxWebPlayerScriptBytes = 8 * 1024 * 1024;
    private const int MaxCacheEntries = 128;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly SemaphoreSlim _musixmatchTokenLock = new(1, 1);
    private readonly SemaphoreSlim _findTracksHashLock = new(1, 1);
    private readonly SemaphoreSlim _canvasHashLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private TotpConfig? _totpConfig;
    private DateTimeOffset _totpConfigExpiresAtUtc;
    private AccessTokenCache? _accessToken;
    private string? _musixmatchToken;
    private DateTimeOffset _musixmatchTokenExpiresAtUtc;
    private string _findTracksHash = DefaultFindTracksHash;
    private string _canvasHash = DefaultCanvasHash;

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

            _cache.TryRemove(cacheKey, out _);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", "Starting Spotify Canvas lookup");
            string? accessToken = await GetAccessTokenAsync(sessionCookie, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken))
            {
                RuntimeLog.Debug("SPOTIFY-CANVAS", "Spotify access token was unavailable");
                return null;
            }

            RuntimeLog.Debug("SPOTIFY-CANVAS", "Spotify access token is ready");

            string? trackId = await ResolveTrackIdAsync(
                trackName,
                artistName,
                duration,
                accessToken,
                timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(trackId))
                return null;

            Uri? canvasUri = await FetchCanvasUriAsync(trackId, accessToken, timeoutCts.Token).ConfigureAwait(false);
            if (canvasUri != null)
            {
                TrimCacheIfNeeded();
                _cache[cacheKey] = new CacheEntry(canvasUri, DateTimeOffset.UtcNow + CacheLifetime);
            }

            return canvasUri;
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                cancellationToken.IsCancellationRequested
                    ? "Canvas lookup was superseded by a media change"
                    : $"Canvas lookup timed out after {RequestTimeout.TotalSeconds:0} seconds");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SPOTIFY-CANVAS", $"Canvas lookup failed: {ex.GetType().Name}: {ex.Message}");
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
                await GetAccessTokenAsync(sessionCookie, timeoutCts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Spotify session validation failed: {ex.Message}");
            return false;
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
        _accessToken = null;
    }

    private async Task<string?> ResolveTrackIdAsync(
        string trackName,
        string artistName,
        TimeSpan duration,
        string accessToken,
        CancellationToken token)
    {
        string? trackId = await ResolveTrackIdFromPathfinderAsync(
            trackName,
            artistName,
            accessToken,
            token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(trackId))
            return trackId;

        RuntimeLog.Debug("SPOTIFY-CANVAS", "Spotify catalog lookup did not resolve the track; trying Musixmatch metadata");
        return await ResolveTrackIdFromMusixmatchAsync(
            trackName,
            artistName,
            duration,
            token).ConfigureAwait(false);
    }

    private async Task<string?> ResolveTrackIdFromPathfinderAsync(
        string trackName,
        string artistName,
        string accessToken,
        CancellationToken token)
    {
        string hash = _findTracksHash;
        PathfinderQueryResult queryResult = await QueryPathfinderAsync(
                trackName, artistName, accessToken, hash, token)
            .ConfigureAwait(false);
        string? json = queryResult.Json;

        if (queryResult.QueryMetadataRejected ||
            (json != null && ContainsPersistedQueryNotFound(json)))
        {
            string? refreshedHash = await RefreshFindTracksHashAsync(hash, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(refreshedHash))
            {
                queryResult = await QueryPathfinderAsync(
                        trackName, artistName, accessToken, refreshedHash, token)
                    .ConfigureAwait(false);
                json = queryResult.Json;
            }
        }

        string? trackId = json == null
            ? null
            : ParsePathfinderTrackId(json, trackName, artistName);
        RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
            string.IsNullOrEmpty(trackId)
                ? "Spotify catalog lookup returned no matching track ID"
                : $"Resolved Spotify track ID from Spotify catalog: {trackId}");
        return trackId;
    }

    private async Task<PathfinderQueryResult> QueryPathfinderAsync(
        string trackName,
        string artistName,
        string accessToken,
        string hash,
        CancellationToken token)
    {
        string query = string.IsNullOrWhiteSpace(artistName)
            ? trackName.Trim()
            : $"{trackName.Trim()} {artistName.Trim()}";
        string payload = JsonSerializer.Serialize(new
        {
            variables = new { query, limit = 5, offset = 0 },
            operationName = "findTracks",
            extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, PathfinderUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
        request.Headers.Referrer = SpotifyWebUri;

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        bool queryMetadataRejected = response.StatusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed;
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                $"Spotify catalog returned HTTP {(int)response.StatusCode}; " +
                $"retryAfter={response.Headers.RetryAfter?.ToString() ?? "none"}");
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token).ConfigureAwait(false);
        string? json = bytes == null ? null : Encoding.UTF8.GetString(bytes);
        return new PathfinderQueryResult(json, queryMetadataRejected);
    }

    private async Task<string?> RefreshFindTracksHashAsync(string failedHash, CancellationToken token)
    {
        await _findTracksHashLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_findTracksHash.Equals(failedHash, StringComparison.Ordinal))
                return _findTracksHash;

            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, SpotifyWebUri);
            pageRequest.Headers.UserAgent.ParseAdd(MobileWebUserAgent);
            string? html = await SendForStringAsync(pageRequest, token).ConfigureAwait(false);
            Match scriptMatch = html == null ? Match.Empty : MobileWebPlayerScriptRegex.Match(html);
            if (!scriptMatch.Success ||
                !Uri.TryCreate(scriptMatch.Value, UriKind.Absolute, out var scriptUri) ||
                scriptUri.Scheme != Uri.UriSchemeHttps ||
                !scriptUri.Host.Equals("open.spotifycdn.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var scriptRequest = new HttpRequestMessage(HttpMethod.Get, scriptUri);
            scriptRequest.Headers.UserAgent.ParseAdd(MobileWebUserAgent);
            string? script = await SendForStringAsync(scriptRequest, token).ConfigureAwait(false);
            Match hashMatch = script == null ? Match.Empty : FindTracksHashRegex.Match(script);
            if (!hashMatch.Success)
                return null;

            _findTracksHash = hashMatch.Groups["hash"].Value;
            RuntimeLog.Debug("SPOTIFY-CANVAS", "Refreshed Spotify catalog query metadata");
            return _findTracksHash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Unable to refresh Spotify catalog metadata: {ex.Message}");
            return null;
        }
        finally
        {
            _findTracksHashLock.Release();
        }
    }

    private static bool ContainsPersistedQueryNotFound(string json) =>
        json.Contains("PersistedQueryNotFound", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ResolveTrackIdFromMusixmatchAsync(
        string trackName,
        string artistName,
        TimeSpan duration,
        CancellationToken token)
    {
        string? userToken = await GetMusixmatchTokenAsync(token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(userToken))
            return null;

        int durationSeconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));
        string path = "ws/1.1/macro.subtitles.get?format=json" +
                      "&namespace=lyrics_richsynched&subtitle_format=mxm" +
                      "&app_id=web-desktop-app-v1.0" +
                      $"&usertoken={Uri.EscapeDataString(userToken)}" +
                      $"&q_artist={Uri.EscapeDataString(artistName)}" +
                      $"&q_artists={Uri.EscapeDataString(artistName)}" +
                      $"&q_track={Uri.EscapeDataString(trackName)}" +
                      $"&q_duration={durationSeconds}&f_subtitle_length={durationSeconds}";

        using var request = CreateMusixmatchRequest(new Uri(MusixmatchBaseUri, path));
        string? json = await SendForStringAsync(request, token).ConfigureAwait(false);
        string? trackId = json == null
            ? null
            : ParseTrackId(json, trackName, artistName, duration);
        RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
            string.IsNullOrEmpty(trackId)
                ? "Musixmatch metadata did not contain a matching Spotify track ID"
                : $"Resolved Spotify track ID from Musixmatch: {trackId}");
        return trackId;
    }

    private async Task<string?> GetMusixmatchTokenAsync(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(_musixmatchToken) &&
            _musixmatchTokenExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return _musixmatchToken;
        }

        await _musixmatchTokenLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_musixmatchToken) &&
                _musixmatchTokenExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return _musixmatchToken;
            }

            using var request = CreateMusixmatchRequest(new Uri(
                MusixmatchBaseUri,
                "ws/1.1/token.get?app_id=web-desktop-app-v1.0"));
            string? json = await SendForStringAsync(request, token).ConfigureAwait(false);
            if (json == null)
            {
                RuntimeLog.Debug("SPOTIFY-CANVAS", "Musixmatch token response was empty");
                return null;
            }

            using var document = JsonDocument.Parse(json);
            string? userToken = FindStringProperty(document.RootElement, "user_token", depth: 0);
            if (string.IsNullOrWhiteSpace(userToken))
            {
                double? serviceStatus = FindNumberProperty(document.RootElement, "status_code", depth: 0);
                RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                    $"Musixmatch did not issue a user token; serviceStatus={serviceStatus?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
                    $"root={document.RootElement.ValueKind}, responseLength={json.Length}");
                return null;
            }

            _musixmatchToken = userToken;
            _musixmatchTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(9);
            RuntimeLog.Debug("SPOTIFY-CANVAS", "Musixmatch metadata token is ready");
            return userToken;
        }
        finally
        {
            _musixmatchTokenLock.Release();
        }
    }

    private static HttpRequestMessage CreateMusixmatchRequest(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Cookie", "AWSELBCORS=0; AWSELB=0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<Uri?> FetchCanvasUriAsync(string trackId, string accessToken, CancellationToken token)
    {
        CanvasLookupResult pathfinder = await FetchCanvasFromPathfinderAsync(trackId, accessToken, token)
            .ConfigureAwait(false);
        if (pathfinder.IsAuthoritative)
            return pathfinder.CanvasUri;

        RuntimeLog.Debug("SPOTIFY-CANVAS", "Spotify Canvas query unavailable; trying legacy Canvas endpoint");
        return await FetchLegacyCanvasUriAsync(trackId, accessToken, token).ConfigureAwait(false);
    }

    private async Task<CanvasLookupResult> FetchCanvasFromPathfinderAsync(
        string trackId,
        string accessToken,
        CancellationToken token)
    {
        string hash = _canvasHash;
        CanvasPathfinderQueryResult queryResult = await QueryCanvasPathfinderAsync(
                trackId, accessToken, hash, token)
            .ConfigureAwait(false);

        if (queryResult.QueryMetadataRejected ||
            (queryResult.Json != null && ContainsPersistedQueryNotFound(queryResult.Json)))
        {
            string? refreshedHash = await RefreshCanvasHashAsync(hash, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(refreshedHash))
            {
                queryResult = await QueryCanvasPathfinderAsync(
                        trackId, accessToken, refreshedHash, token)
                    .ConfigureAwait(false);
            }
        }

        if (!queryResult.TransportSucceeded || queryResult.Json == null ||
            !TryParsePathfinderCanvasResponse(queryResult.Json, out Uri? canvasUri))
        {
            return new CanvasLookupResult(IsAuthoritative: false, CanvasUri: null);
        }

        RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
            canvasUri == null
                ? "Spotify Canvas query confirmed that the track has no Canvas"
                : $"Canvas video resolved from {canvasUri.Host}");
        return new CanvasLookupResult(IsAuthoritative: true, CanvasUri: canvasUri);
    }

    private async Task<CanvasPathfinderQueryResult> QueryCanvasPathfinderAsync(
        string trackId,
        string accessToken,
        string hash,
        CancellationToken token)
    {
        string variables = JsonSerializer.Serialize(new { trackUri = $"spotify:track:{trackId}" });
        string extensions = JsonSerializer.Serialize(new
        {
            persistedQuery = new { version = 1, sha256Hash = hash }
        });
        var endpoint = new UriBuilder(CanvasPathfinderUri)
        {
            Query = "operationName=canvas" +
                    $"&variables={Uri.EscapeDataString(variables)}" +
                    $"&extensions={Uri.EscapeDataString(extensions)}"
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        bool queryMetadataRejected = response.StatusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed;
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                $"Spotify Canvas query returned HTTP {(int)response.StatusCode}; " +
                $"retryAfter={response.Headers.RetryAfter?.ToString() ?? "none"}");
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token).ConfigureAwait(false);
        string? json = bytes == null ? null : Encoding.UTF8.GetString(bytes);
        return new CanvasPathfinderQueryResult(json, queryMetadataRejected, response.IsSuccessStatusCode);
    }

    private async Task<string?> RefreshCanvasHashAsync(string failedHash, CancellationToken token)
    {
        await _canvasHashLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_canvasHash.Equals(failedHash, StringComparison.Ordinal))
                return _canvasHash;

            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, SpotifyWebUri);
            pageRequest.Headers.UserAgent.ParseAdd(DesktopWebUserAgent);
            string? html = await SendForStringAsync(pageRequest, token).ConfigureAwait(false);
            Match scriptMatch = html == null ? Match.Empty : DesktopWebPlayerScriptRegex.Match(html);
            if (!scriptMatch.Success ||
                !Uri.TryCreate(scriptMatch.Value, UriKind.Absolute, out var scriptUri) ||
                scriptUri.Scheme != Uri.UriSchemeHttps ||
                !scriptUri.Host.Equals("open.spotifycdn.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var scriptRequest = new HttpRequestMessage(HttpMethod.Get, scriptUri);
            scriptRequest.Headers.UserAgent.ParseAdd(DesktopWebUserAgent);
            string? script = await SendForStringAsync(
                scriptRequest, token, MaxWebPlayerScriptBytes).ConfigureAwait(false);
            Match hashMatch = script == null ? Match.Empty : CanvasHashRegex.Match(script);
            if (!hashMatch.Success)
                return null;

            _canvasHash = hashMatch.Groups["hash"].Value;
            RuntimeLog.Debug("SPOTIFY-CANVAS", "Refreshed Spotify Canvas query metadata");
            return _canvasHash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Unable to refresh Spotify Canvas metadata: {ex.Message}");
            return null;
        }
        finally
        {
            _canvasHashLock.Release();
        }
    }

    internal static bool TryParsePathfinderCanvasResponse(string json, out Uri? canvasUri)
    {
        canvasUri = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (TryGetProperty(document.RootElement, "errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                return false;
            }

            if (!TryGetProperty(document.RootElement, "data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(data, "trackUnion", out var track) ||
                track.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(track, "canvas", out var canvas))
            {
                return false;
            }

            if (canvas.ValueKind == JsonValueKind.Null)
                return true;
            if (canvas.ValueKind != JsonValueKind.Object)
                return false;

            return TryCreateCanvasUri(GetDirectString(canvas, "url"), out canvasUri);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<Uri?> FetchLegacyCanvasUriAsync(string trackId, string accessToken, CancellationToken token)
    {
        byte[] requestBytes = BuildCanvasRequest(trackId);
        using var request = new HttpRequestMessage(HttpMethod.Post, LegacyCanvasUri)
        {
            Content = new ByteArrayContent(requestBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/protobuf"));
        request.Headers.AcceptLanguage.ParseAdd("en");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Spotify/9.0.34.593 iOS/18.4 (iPhone15,3)");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                $"Canvas endpoint returned HTTP {(int)response.StatusCode}");
            return null;
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token).ConfigureAwait(false);
        Uri? canvasUri = bytes == null ? null : ParseCanvasResponse(bytes);
        RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
            canvasUri == null
                ? $"Legacy Canvas response contained no playable video ({bytes?.Length ?? 0} bytes)"
                : $"Legacy Canvas video resolved from {canvasUri.Host}");
        return canvasUri;
    }

    private async Task<string?> GetAccessTokenAsync(string sessionCookie, CancellationToken token)
    {
        string cookieHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionCookie)));
        AccessTokenCache? cached = _accessToken;
        if (cached != null &&
            cached.CookieHash == cookieHash &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        await _authLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = _accessToken;
            if (cached != null &&
                cached.CookieHash == cookieHash &&
                cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return cached.Token;
            }

            TotpConfig? config = await GetTotpConfigAsync(token).ConfigureAwait(false);
            if (config == null)
                return null;

            long localTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long serverTimeMs = await GetServerTimeMsAsync(sessionCookie, localTimeMs, token).ConfigureAwait(false);
            string localTotp = GenerateTotp(config.Secret, localTimeMs);

            // Matches the token payload used by Paxsenix0/Spotify-Canvas-API.
            string serverTotp = GenerateTotp(config.Secret, (long)Math.Floor(serverTimeMs / 30d));
            var endpoint = new UriBuilder(TokenUri)
            {
                Query = "reason=init&productType=mobile-web-player" +
                        $"&totp={Uri.EscapeDataString(localTotp)}" +
                        $"&totpVer={Uri.EscapeDataString(config.Version)}" +
                        $"&totpServer={Uri.EscapeDataString(serverTotp)}"
            }.Uri;

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            AddSpotifyWebHeaders(request, sessionCookie);
            string? json = await SendForStringAsync(request, token).ConfigureAwait(false);
            if (json == null)
                return null;

            using var document = JsonDocument.Parse(json);
            string? accessToken = GetDirectString(document.RootElement, "accessToken", "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            if (TryGetProperty(document.RootElement, "accessTokenExpirationTimestampMs", out var expiration) &&
                expiration.ValueKind == JsonValueKind.Number &&
                expiration.TryGetInt64(out long expirationMs))
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expirationMs);
            }

            _accessToken = new AccessTokenCache(cookieHash, accessToken, expiresAt);
            return accessToken;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<TotpConfig?> GetTotpConfigAsync(CancellationToken token)
    {
        if (_totpConfig != null && _totpConfigExpiresAtUtc > DateTimeOffset.UtcNow)
            return _totpConfig;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SecretsUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            string? json = await SendForStringAsync(request, token).ConfigureAwait(false);
            if (json == null)
                return _totpConfig;

            using var document = JsonDocument.Parse(json);
            int newestVersion = int.MinValue;
            int[]? encryptedValues = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) ||
                    version <= newestVersion ||
                    property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var values = new List<int>();
                bool valid = true;
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (!item.TryGetInt32(out int value))
                    {
                        valid = false;
                        break;
                    }
                    values.Add(value);
                }

                if (valid && values.Count > 0)
                {
                    newestVersion = version;
                    encryptedValues = values.ToArray();
                }
            }

            if (encryptedValues == null)
                return _totpConfig;

            var decoded = new StringBuilder(encryptedValues.Length * 2);
            for (int i = 0; i < encryptedValues.Length; i++)
            {
                int value = encryptedValues[i] ^ ((i % 33) + 9);
                decoded.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            _totpConfig = new TotpConfig(
                newestVersion.ToString(CultureInfo.InvariantCulture),
                Encoding.UTF8.GetBytes(decoded.ToString()));
            _totpConfigExpiresAtUtc = DateTimeOffset.UtcNow + TotpSecretLifetime;
            return _totpConfig;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Unable to refresh Spotify token secret: {ex.Message}");
            return _totpConfig;
        }
    }

    private async Task<long> GetServerTimeMsAsync(
        string sessionCookie,
        long fallbackTimeMs,
        CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ServerTimeUri);
            AddSpotifyWebHeaders(request, sessionCookie);
            string? json = await SendForStringAsync(request, token).ConfigureAwait(false);
            if (json == null)
                return fallbackTimeMs;

            using var document = JsonDocument.Parse(json);
            if (TryGetProperty(document.RootElement, "serverTime", out var serverTime) &&
                serverTime.ValueKind == JsonValueKind.Number &&
                serverTime.TryGetDouble(out double seconds) &&
                double.IsFinite(seconds))
            {
                return checked((long)(seconds * 1000d));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.Debug("SPOTIFY-CANVAS", () => $"Spotify server time unavailable: {ex.Message}");
        }

        return fallbackTimeMs;
    }

    private async Task<string?> SendForStringAsync(
        HttpRequestMessage request,
        CancellationToken token,
        int maxResponseBytes = MaxResponseBytes)
    {
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string retryAfter = response.Headers.RetryAfter?.ToString() ?? "none";
            RuntimeLog.Debug("SPOTIFY-CANVAS", () =>
                $"Remote service returned HTTP {(int)response.StatusCode} for {request.RequestUri?.AbsolutePath}; " +
                $"retryAfter={retryAfter}");
            return null;
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token, maxResponseBytes).ConfigureAwait(false);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]?> ReadLimitedBytesAsync(
        HttpResponseMessage response,
        CancellationToken token,
        int maxResponseBytes = MaxResponseBytes)
    {
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxResponseBytes)
            return null;

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        return bytes.Length > 0 && bytes.Length <= maxResponseBytes ? bytes : null;
    }

    private static void AddSpotifyWebHeaders(HttpRequestMessage request, string sessionCookie)
    {
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com/");
        request.Headers.Referrer = new Uri("https://open.spotify.com/");
        request.Headers.TryAddWithoutValidation("Cookie", $"sp_dc={sessionCookie}");
    }

    private static string GenerateTotp(byte[] secret, long timestampMs)
    {
        long counter = timestampMs / 1000 / 30;
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        using var hmac = new HMACSHA1(secret);
        byte[] hash = hmac.ComputeHash(counterBytes.ToArray());
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    internal static byte[] BuildCanvasRequest(string trackId)
    {
        string trackUri = $"spotify:track:{trackId}";
        byte[] uriBytes = Encoding.UTF8.GetBytes(trackUri);

        using var track = new MemoryStream();
        track.WriteByte(0x0A); // Track.track_uri, field 1, length-delimited.
        WriteVarint(track, (ulong)uriBytes.Length);
        track.Write(uriBytes);

        byte[] trackBytes = track.ToArray();
        using var request = new MemoryStream();
        request.WriteByte(0x0A); // CanvasRequest.tracks, field 1, length-delimited.
        WriteVarint(request, (ulong)trackBytes.Length);
        request.Write(trackBytes);
        return request.ToArray();
    }

    internal static Uri? ParseCanvasResponse(ReadOnlySpan<byte> protobuf)
    {
        int offset = 0;
        while (offset < protobuf.Length)
        {
            if (!TryReadVarint(protobuf, ref offset, out ulong tag))
                return null;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);
            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadLengthDelimited(protobuf, ref offset, out var canvas))
                    return null;

                Uri? uri = ParseCanvasMessage(canvas);
                if (uri != null)
                    return uri;
            }
            else if (!TrySkipField(protobuf, ref offset, wireType))
            {
                return null;
            }
        }

        return null;
    }

    private static Uri? ParseCanvasMessage(ReadOnlySpan<byte> canvas)
    {
        int offset = 0;
        while (offset < canvas.Length)
        {
            if (!TryReadVarint(canvas, ref offset, out ulong tag))
                return null;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);
            if (fieldNumber == 2 && wireType == 2)
            {
                if (!TryReadLengthDelimited(canvas, ref offset, out var value))
                    return null;

                if (TryCreateCanvasUri(Encoding.UTF8.GetString(value), out var uri))
                    return uri;
            }
            else if (!TrySkipField(canvas, ref offset, wireType))
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryReadLengthDelimited(
        ReadOnlySpan<byte> data,
        ref int offset,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadVarint(data, ref offset, out ulong length) || length > int.MaxValue)
            return false;

        int intLength = (int)length;
        if (offset < 0 || intLength < 0 || offset > data.Length - intLength)
            return false;

        value = data.Slice(offset, intLength);
        offset += intLength;
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return true;
        }
        return false;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref offset, out _);
            case 1:
                if (offset > data.Length - 8) return false;
                offset += 8;
                return true;
            case 2:
                return TryReadLengthDelimited(data, ref offset, out _);
            case 5:
                if (offset > data.Length - 4) return false;
                offset += 4;
                return true;
            default:
                return false;
        }
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    internal static string? ParsePathfinderTrackId(
        string json,
        string expectedTrack,
        string expectedArtist)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (!TryGetProperty(document.RootElement, "data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(data, "searchV2", out var search) ||
                search.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(search, "tracksV2", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(tracks, "items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestId = null;
            int bestScore = int.MinValue;
            foreach (var result in items.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(result, "item", out var item) ||
                    item.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(item, "data", out var track) ||
                    track.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? typeName = GetDirectString(track, "__typename");
                if (!string.IsNullOrEmpty(typeName) &&
                    !typeName.Equals("Track", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? id = GetTrackId(track);
                string? title = GetDirectString(track, "name");
                int titleScore = MatchScore(title, expectedTrack, exact: 100, contains: 72);
                if (id == null || titleScore < 72)
                    continue;

                int artistScore = 0;
                if (!string.IsNullOrWhiteSpace(expectedArtist))
                {
                    IReadOnlyList<string> artists = GetPathfinderArtistNames(track);
                    foreach (string artist in artists)
                    {
                        artistScore = Math.Max(
                            artistScore,
                            MatchScore(artist, expectedArtist, exact: 35, contains: 22));
                    }

                    if (artists.Count > 1)
                    {
                        artistScore = Math.Max(
                            artistScore,
                            MatchScore(string.Join(" ", artists), expectedArtist, exact: 35, contains: 22));
                    }

                    // Exact-title collisions are common. Do not return a different
                    // artist merely because Spotify ranked it first.
                    if (artistScore == 0)
                        continue;
                }

                int score = titleScore + artistScore;
                if (score > bestScore)
                {
                    bestId = id;
                    bestScore = score;
                }
            }

            return bestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetPathfinderArtistNames(JsonElement track)
    {
        var names = new List<string>();
        if (!TryGetProperty(track, "artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(artists, "items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(item, "profile", out var profile) ||
                profile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = GetDirectString(profile, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    internal static string? ParseTrackId(
        string json,
        string expectedTrack,
        string expectedArtist,
        TimeSpan expectedDuration)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var candidates = new List<TrackCandidate>();
            CollectTrackCandidates(document.RootElement, candidates, depth: 0);

            TrackCandidate? best = null;
            int bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                int titleScore = MatchScore(candidate.Title, expectedTrack, exact: 100, contains: 72);
                if (titleScore < 72)
                    continue;

                int score = titleScore;
                if (!string.IsNullOrWhiteSpace(expectedArtist))
                    score += MatchScore(candidate.Artist, expectedArtist, exact: 35, contains: 22);

                if (candidate.Duration > TimeSpan.Zero && expectedDuration > TimeSpan.Zero)
                {
                    double delta = Math.Abs((candidate.Duration - expectedDuration).TotalSeconds);
                    score += delta <= 4 ? 12 : delta <= 12 ? 5 : delta > 45 ? -15 : 0;
                }

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectTrackCandidates(JsonElement element, List<TrackCandidate> candidates, int depth)
    {
        if (depth > 24)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? id = GetTrackId(element);
            string? title = GetDirectString(element, "trackName", "track_name", "title", "name");
            if (id != null && !string.IsNullOrWhiteSpace(title))
            {
                candidates.Add(new TrackCandidate(
                    id,
                    title,
                    GetArtistName(element) ?? "",
                    GetDuration(element)));
            }

            foreach (var property in element.EnumerateObject())
                CollectTrackCandidates(property.Value, candidates, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectTrackCandidates(item, candidates, depth + 1);
        }
    }

    private static string? GetTrackId(JsonElement element)
    {
        foreach (string propertyName in new[] { "trackUri", "track_uri", "uri", "url", "externalUrl" })
        {
            string? parsed = ExtractTrackId(GetDirectString(element, propertyName));
            if (parsed != null)
                return parsed;
        }

        foreach (string propertyName in new[]
                 {
                     "track_spotify_id", "trackId", "track_id", "spotifyId", "spotify_id", "id"
                 })
        {
            string? value = GetDirectString(element, propertyName);
            if (IsSpotifyId(value))
                return value;
        }

        return null;
    }

    private static string? ExtractTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        const string uriPrefix = "spotify:track:";
        int uriIndex = value.IndexOf(uriPrefix, StringComparison.OrdinalIgnoreCase);
        if (uriIndex >= 0)
        {
            string possibleId = value[(uriIndex + uriPrefix.Length)..].Split('?', '/', '&')[0];
            if (IsSpotifyId(possibleId))
                return possibleId;
        }

        const string pathPrefix = "/track/";
        int pathIndex = value.IndexOf(pathPrefix, StringComparison.OrdinalIgnoreCase);
        if (pathIndex >= 0)
        {
            string possibleId = value[(pathIndex + pathPrefix.Length)..].Split('?', '/', '&')[0];
            if (IsSpotifyId(possibleId))
                return possibleId;
        }

        return IsSpotifyId(value) ? value : null;
    }

    private static bool IsSpotifyId(string? value) =>
        value is { Length: 22 } && value.All(ch =>
            ch is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static string? GetArtistName(JsonElement element)
    {
        string? direct = GetDirectString(element, "artistName", "artist_name", "author", "subtitle");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (TryGetProperty(element, "artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString();
            if (artist.ValueKind == JsonValueKind.Object)
                return GetDirectString(artist, "name", "artistName", "artist_name");
        }

        if (TryGetProperty(element, "artists", out var artists) && artists.ValueKind == JsonValueKind.Object)
        {
            string? nestedName = FindStringProperty(artists, "name", depth: 0);
            if (!string.IsNullOrWhiteSpace(nestedName))
                return nestedName;
        }

        if (artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in artists.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string? name = GetDirectString(item, "name", "artistName", "artist_name");
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
        }

        return null;
    }

    private static TimeSpan GetDuration(JsonElement element)
    {
        foreach (string name in new[] { "durationMs", "duration_ms", "durationMillis", "duration_millis" })
        {
            if (TryGetNumber(element, name, out double milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
        }

        foreach (string name in new[] { "duration", "durationSeconds", "duration_seconds", "track_length" })
        {
            if (TryGetNumber(element, name, out double seconds) && seconds > 0)
                return seconds > 10_000 ? TimeSpan.FromMilliseconds(seconds) : TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.Zero;
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDouble(out value);
        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryCreateCanvasUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps)
            return false;

        bool trustedHost = candidate.Host.Equals("scdn.co", StringComparison.OrdinalIgnoreCase) ||
                           candidate.Host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase);
        bool isMp4 = candidate.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        if (!trustedHost || !isMp4)
            return false;

        uri = candidate;
        return true;
    }

    private static string? GetDirectString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }
        return null;
    }

    private static string? FindStringProperty(JsonElement element, string name, int depth)
    {
        if (depth > 16)
            return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                string? nested = FindStringProperty(property.Value, name, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? nested = FindStringProperty(item, name, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static double? FindNumberProperty(JsonElement element, string name, int depth)
    {
        if (depth > 16)
            return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetDouble(out double value))
                {
                    return value;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                double? nested = FindNumberProperty(property.Value, name, depth + 1);
                if (nested.HasValue)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                double? nested = FindNumberProperty(item, name, depth + 1);
                if (nested.HasValue)
                    return nested;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static int MatchScore(string? candidate, string expected, int exact, int contains)
    {
        string normalizedCandidate = NormalizeForMatch(candidate);
        string normalizedExpected = NormalizeForMatch(expected);
        if (normalizedCandidate.Length == 0 || normalizedExpected.Length == 0)
            return 0;
        if (normalizedCandidate == normalizedExpected)
            return exact;
        if (normalizedCandidate.Contains(normalizedExpected, StringComparison.Ordinal) ||
            normalizedExpected.Contains(normalizedCandidate, StringComparison.Ordinal))
            return contains;
        return 0;
    }

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char ch in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
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

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count < MaxCacheEntries)
            return;

        foreach (var entry in _cache.OrderBy(pair => pair.Value.ExpiresAtUtc).Take(_cache.Count - MaxCacheEntries + 1))
            _cache.TryRemove(entry.Key, out _);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.8 SpotifyCanvas");
        return client;
    }

    public void Dispose()
    {
        _authLock.Dispose();
        _musixmatchTokenLock.Dispose();
        _findTracksHashLock.Dispose();
        _canvasHashLock.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record CacheEntry(Uri CanvasUri, DateTimeOffset ExpiresAtUtc);
    private sealed record TrackCandidate(string Id, string Title, string Artist, TimeSpan Duration);
    private sealed record TotpConfig(string Version, byte[] Secret);
    private sealed record AccessTokenCache(string CookieHash, string Token, DateTimeOffset ExpiresAtUtc);
    private sealed record PathfinderQueryResult(string? Json, bool QueryMetadataRejected);
    private sealed record CanvasPathfinderQueryResult(
        string? Json,
        bool QueryMetadataRejected,
        bool TransportSucceeded);
    private sealed record CanvasLookupResult(bool IsAuthoritative, Uri? CanvasUri);
}
