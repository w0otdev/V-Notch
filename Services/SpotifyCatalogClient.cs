using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static VNotch.Services.SpotifyHttpResponseReader;
using static VNotch.Services.SpotifyQueryMetadataProvider;
using static VNotch.Services.SpotifyTrackMatcher;

namespace VNotch.Services;

internal sealed partial class SpotifyCatalogClient
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_catalog_client_build_query(
        ReadOnlySpan<char> trackName,
        int trackLen,
        ReadOnlySpan<char> artistName,
        int artistLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_spotify_catalog_client_is_metadata_rejected_status(
        int statusCode,
        out byte outRejected);

    private const string LogCategory = "SPOTIFY-CANVAS";
    private const string JsonContentType = "application/json";
    private const string SpotifyWebOrigin = "https://open.spotify.com";
    private static readonly Uri SpotifyWebUri = new("https://open.spotify.com/");
    private static readonly Uri PathfinderUri = new("https://api-partner.spotify.com/pathfinder/v2/query");
    private readonly HttpClient _http;
    private readonly SpotifyQueryMetadataProvider _metadata;

    internal SpotifyCatalogClient(HttpClient http, SpotifyQueryMetadataProvider metadata)
    {
        _http = http;
        _metadata = metadata;
    }

    internal async Task<string?> ResolveTrackIdAsync(
        string trackName,
        string artistName,
        string accessToken,
        CancellationToken token)
    {
        string hash = _metadata.FindTracksHash;
        PathfinderQueryResult queryResult = await QueryPathfinderAsync(
                trackName, artistName, accessToken, hash, token)
            .ConfigureAwait(false);
        string? json = queryResult.Json;

        if (queryResult.QueryMetadataRejected ||
            (json != null && ContainsPersistedQueryNotFound(json)))
        {
            string? refreshedHash = await _metadata.RefreshAsync(SpotifyQuery.FindTracks, hash, token).ConfigureAwait(false);
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
        RuntimeLog.Debug(LogCategory, () =>
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
        string query = BuildQuery(trackName, artistName);
        string payload = JsonSerializer.Serialize(new
        {
            variables = new { query, limit = 5, offset = 0 },
            operationName = "findTracks",
            extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, PathfinderUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, JsonContentType)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonContentType));
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", SpotifyWebOrigin);
        request.Headers.Referrer = SpotifyWebUri;

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        bool queryMetadataRejected = vn_spotify_catalog_client_is_metadata_rejected_status((int)response.StatusCode, out byte outRejected) == 0
            ? outRejected != 0
            : response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed;
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Debug(LogCategory, () =>
                $"Spotify catalog returned HTTP {(int)response.StatusCode}; " +
                $"retryAfter={response.Headers.RetryAfter?.ToString() ?? "none"}");
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token).ConfigureAwait(false);
        string? json = bytes == null ? null : Encoding.UTF8.GetString(bytes);
        return new PathfinderQueryResult(json, queryMetadataRejected);
    }

    private static string BuildQuery(string? trackName, string? artistName)
    {
        ReadOnlySpan<char> tSpan = trackName is not null ? trackName.AsSpan() : ReadOnlySpan<char>.Empty;
        ReadOnlySpan<char> aSpan = artistName is not null ? artistName.AsSpan() : ReadOnlySpan<char>.Empty;
        int maxLen = tSpan.Length + aSpan.Length + 2;
        if (maxLen <= 512)
        {
            Span<char> buf = stackalloc char[512];
            if (vn_spotify_catalog_client_build_query(tSpan, tSpan.Length, aSpan, aSpan.Length, buf, buf.Length, out int outLen) == 0)
            {
                return new string(buf[..outLen]);
            }
        }
        string t = (trackName ?? string.Empty).Trim();
        string a = (artistName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(a) ? t : $"{t} {a}";
    }

    private sealed record PathfinderQueryResult(string? Json, bool QueryMetadataRejected);
}
