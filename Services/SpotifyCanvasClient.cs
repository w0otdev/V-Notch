using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static VNotch.Services.SpotifyCanvasProtocol;
using static VNotch.Services.SpotifyHttpResponseReader;
using static VNotch.Services.SpotifyJson;
using static VNotch.Services.SpotifyQueryMetadataProvider;

namespace VNotch.Services;

internal sealed class SpotifyCanvasClient
{
    private const string LogCategory = "SPOTIFY-CANVAS";
    private const string JsonContentType = "application/json";
    private static readonly Uri CanvasPathfinderUri = new("https://api-partner.spotify.com/pathfinder/v1/query");
    private static readonly Uri LegacyCanvasUri = new("https://spclient.wg.spotify.com/canvaz-cache/v0/canvases");
    private readonly HttpClient _http;
    private readonly SpotifyQueryMetadataProvider _metadata;

    internal SpotifyCanvasClient(HttpClient http, SpotifyQueryMetadataProvider metadata)
    {
        _http = http;
        _metadata = metadata;
    }

    internal async Task<Uri?> FetchAsync(string trackId, string accessToken, CancellationToken token)
    {
        CanvasLookupResult pathfinder = await FetchCanvasFromPathfinderAsync(trackId, accessToken, token)
            .ConfigureAwait(false);
        if (pathfinder.IsAuthoritative)
            return pathfinder.CanvasUri;

        RuntimeLog.Debug(LogCategory, "Spotify Canvas query unavailable; trying legacy Canvas endpoint");
        return await FetchLegacyCanvasUriAsync(trackId, accessToken, token).ConfigureAwait(false);
    }

    private async Task<CanvasLookupResult> FetchCanvasFromPathfinderAsync(
        string trackId,
        string accessToken,
        CancellationToken token)
    {
        string hash = _metadata.CanvasHash;
        CanvasPathfinderQueryResult queryResult = await QueryCanvasPathfinderAsync(
                trackId, accessToken, hash, token)
            .ConfigureAwait(false);

        if (queryResult.QueryMetadataRejected ||
            (queryResult.Json != null && ContainsPersistedQueryNotFound(queryResult.Json)))
        {
            string? refreshedHash = await _metadata.RefreshAsync(SpotifyQuery.Canvas, hash, token).ConfigureAwait(false);
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

        RuntimeLog.Debug(LogCategory, () =>
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonContentType));

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        bool queryMetadataRejected = response.StatusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed;
        if (!response.IsSuccessStatusCode)
        {
            RuntimeLog.Debug(LogCategory, () =>
                $"Spotify Canvas query returned HTTP {(int)response.StatusCode}; " +
                $"retryAfter={response.Headers.RetryAfter?.ToString() ?? "none"}");
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token).ConfigureAwait(false);
        string? json = bytes == null ? null : Encoding.UTF8.GetString(bytes);
        return new CanvasPathfinderQueryResult(json, queryMetadataRejected, response.IsSuccessStatusCode);
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
            RuntimeLog.Debug(LogCategory, () =>
                $"Canvas endpoint returned HTTP {(int)response.StatusCode}");
            return null;
        }

        byte[]? bytes = await ReadLimitedBytesAsync(response, token).ConfigureAwait(false);
        Uri? canvasUri = bytes == null ? null : ParseCanvasResponse(bytes);
        RuntimeLog.Debug(LogCategory, () =>
            canvasUri == null
                ? $"Legacy Canvas response contained no playable video ({bytes?.Length ?? 0} bytes)"
                : $"Legacy Canvas video resolved from {canvasUri.Host}");
        return canvasUri;
    }

    private sealed record CanvasPathfinderQueryResult(string? Json, bool QueryMetadataRejected, bool TransportSucceeded);
    private sealed record CanvasLookupResult(bool IsAuthoritative, Uri? CanvasUri);
}
