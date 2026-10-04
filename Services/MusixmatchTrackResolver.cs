using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using static VNotch.Services.SpotifyHttpResponseReader;
using static VNotch.Services.SpotifyJson;
using static VNotch.Services.SpotifyTrackMatcher;

namespace VNotch.Services;

internal sealed class MusixmatchTrackResolver : IDisposable
{
    private const string LogCategory = "SPOTIFY-CANVAS";
    private const string JsonContentType = "application/json";
    private static readonly Uri MusixmatchBaseUri = new("https://apic-desktop.musixmatch.com/");
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _musixmatchTokenLock = new(1, 1);
    private string? _musixmatchToken;
    private DateTimeOffset _musixmatchTokenExpiresAtUtc;

    internal MusixmatchTrackResolver(HttpClient http) => _http = http;

    internal async Task<string?> ResolveTrackIdAsync(
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
        string? json = await SendForStringAsync(_http, request, token).ConfigureAwait(false);
        string? trackId = json == null
            ? null
            : ParseTrackId(json, trackName, artistName, duration);
        RuntimeLog.Debug(LogCategory, () =>
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
            string? json = await SendForStringAsync(_http, request, token).ConfigureAwait(false);
            if (json == null)
            {
                RuntimeLog.Debug(LogCategory, "Musixmatch token response was empty");
                return null;
            }

            using var document = JsonDocument.Parse(json);
            string? userToken = FindStringProperty(document.RootElement, "user_token", depth: 0);
            if (string.IsNullOrWhiteSpace(userToken))
            {
                double? serviceStatus = FindNumberProperty(document.RootElement, "status_code", depth: 0);
                RuntimeLog.Debug(LogCategory, () =>
                    $"Musixmatch did not issue a user token; serviceStatus={serviceStatus?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, " +
                    $"root={document.RootElement.ValueKind}, responseLength={json.Length}");
                return null;
            }

            _musixmatchToken = userToken;
            _musixmatchTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(9);
            RuntimeLog.Debug(LogCategory, "Musixmatch metadata token is ready");
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonContentType));
        return request;
    }

    public void Dispose() => _musixmatchTokenLock.Dispose();
}
