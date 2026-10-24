using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using static VNotch.Services.SpotifyHttpResponseReader;
using static VNotch.Services.SpotifyJson;

namespace VNotch.Services;

internal sealed partial class SpotifyTokenProvider : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";
    private const string LogCategory = "SPOTIFY-CANVAS";
    private const string SpotifyWebOrigin = "https://open.spotify.com";
    private static readonly Uri SpotifyWebUri = new("https://open.spotify.com/");
    private static readonly Uri ServerTimeUri = new("https://open.spotify.com/api/server-time");
    private static readonly Uri TokenUri = new("https://open.spotify.com/api/token");
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private AccessTokenCache? _accessToken;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_generate_totp(
        ReadOnlySpan<byte> secretPtr,
        int secretLen,
        long timestampMs,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    internal SpotifyTokenProvider(HttpClient http) => _http = http;
    internal void ClearCache() => _accessToken = null;

    internal Task<string?> GetAccessTokenAsync(string sessionCookie, CancellationToken token)
    {
        AccessTokenCache? cached = _accessToken;
        if (cached != null &&
            string.Equals(cached.SessionCookie, sessionCookie, StringComparison.Ordinal) &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Task;
        }

        return RefreshAccessTokenAsync(sessionCookie, token);
    }

    private async Task<string?> RefreshAccessTokenAsync(string sessionCookie, CancellationToken token)
    {
        await _authLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = _accessToken;
            if (cached != null &&
                string.Equals(cached.SessionCookie, sessionCookie, StringComparison.Ordinal) &&
                cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return cached.Token;
            }

            long localTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long serverTimeMs = await GetServerTimeMsAsync(sessionCookie, localTimeMs, token).ConfigureAwait(false);
            string localTotp = GenerateTotp(SpotifyWebPlayerProtocol.TotpSecret, localTimeMs);

            string serverTotp = GenerateTotp(SpotifyWebPlayerProtocol.TotpSecret, serverTimeMs);
            var endpoint = new UriBuilder(TokenUri)
            {
                Query = "reason=init&productType=mobile-web-player" +
                        $"&totp={Uri.EscapeDataString(localTotp)}" +
                        $"&totpVer={SpotifyWebPlayerProtocol.TotpVersion}" +
                        $"&totpServer={Uri.EscapeDataString(serverTotp)}"
            }.Uri;

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            AddSpotifyWebHeaders(request, sessionCookie);
            string? json = await SendForStringAsync(_http, request, token).ConfigureAwait(false);
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

            _accessToken = new AccessTokenCache(sessionCookie, accessToken, expiresAt, Task.FromResult<string?>(accessToken));
            return accessToken;
        }
        finally
        {
            _authLock.Release();
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
            string? json = await SendForStringAsync(_http, request, token).ConfigureAwait(false);
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
            RuntimeLog.Debug(LogCategory, () => $"Spotify server time unavailable: {ex.Message}");
        }

        return fallbackTimeMs;
    }

    private static void AddSpotifyWebHeaders(HttpRequestMessage request, string sessionCookie)
    {
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", SpotifyWebOrigin);
        request.Headers.Referrer = SpotifyWebUri;
        request.Headers.TryAddWithoutValidation("Cookie", $"sp_dc={sessionCookie}");
    }

    internal static string GenerateTotp(ReadOnlySpan<byte> secret, long timestampMs)
    {
        Span<char> buf = stackalloc char[16];
        vn_spotify_generate_totp(secret, secret.Length, timestampMs, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : "000000";
    }

    public void Dispose() => _authLock.Dispose();

    private sealed record AccessTokenCache(string SessionCookie, string Token, DateTimeOffset ExpiresAtUtc, Task<string?> Task);
}
