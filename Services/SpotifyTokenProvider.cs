using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static VNotch.Services.SpotifyHttpResponseReader;
using static VNotch.Services.SpotifyJson;

namespace VNotch.Services;

internal sealed class SpotifyTokenProvider : IDisposable
{
    private const string LogCategory = "SPOTIFY-CANVAS";
    private const string SpotifyWebOrigin = "https://open.spotify.com";
    private static readonly Uri SpotifyWebUri = new("https://open.spotify.com/");
    private static readonly Uri ServerTimeUri = new("https://open.spotify.com/api/server-time");
    private static readonly Uri TokenUri = new("https://open.spotify.com/api/token");
    private static readonly TotpConfig BundledTotpConfig = new(
        SpotifyWebPlayerProtocol.TotpVersion, SpotifyWebPlayerProtocol.CreateTotpSecret());
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private AccessTokenCache? _accessToken;

    internal SpotifyTokenProvider(HttpClient http) => _http = http;
    internal void ClearCache() => _accessToken = null;

    internal async Task<string?> GetAccessTokenAsync(string sessionCookie, CancellationToken token)
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

            TotpConfig config = BundledTotpConfig;

            long localTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long serverTimeMs = await GetServerTimeMsAsync(sessionCookie, localTimeMs, token).ConfigureAwait(false);
            string localTotp = GenerateTotp(config.Secret, localTimeMs);

            string serverTotp = GenerateTotp(config.Secret, serverTimeMs);
            var endpoint = new UriBuilder(TokenUri)
            {
                Query = "reason=init&productType=mobile-web-player" +
                        $"&totp={Uri.EscapeDataString(localTotp)}" +
                        $"&totpVer={Uri.EscapeDataString(config.Version)}" +
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

            _accessToken = new AccessTokenCache(cookieHash, accessToken, expiresAt);
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
        long counter = timestampMs / 1000 / 30;
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

#pragma warning disable S4790 // Spotify mobile-web-player token generation protocol specifically mandates RFC 6238 HMAC-SHA1
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counterBytes, hash);
#pragma warning restore S4790
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public void Dispose() => _authLock.Dispose();

    private sealed record TotpConfig(string Version, byte[] Secret);
    private sealed record AccessTokenCache(string CookieHash, string Token, DateTimeOffset ExpiresAtUtc);
}
