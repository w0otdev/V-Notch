using System.Net;
using System.Net.Http;

namespace VNotch.Services;

internal sealed partial class LyricsService
{
    private static readonly HttpClient AmllHttp = CreateClient("https://raw.githubusercontent.com", 2000);
    private long _amllRetryUntil;
    internal static string? NormalizeSpotifyTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.StartsWith("spotify:track:", StringComparison.Ordinal))
            value = value[14..];
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                 uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length < 2 || path[^2] != "track") return null;
            value = path[^1];
        }
        return value.Length == 22 && value.All(char.IsAsciiLetterOrDigit) ? value : null;
    }

    private async Task<LyricsResult?> TryAmllAsync(string? trackId, CancellationToken token)
    {
        trackId = NormalizeSpotifyTrackId(trackId);
        if (trackId == null || DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _amllRetryUntil)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_amllClient.Timeout);
        try
        {
            using var response = await _amllClient.GetAsync(
                $"/amll-dev/amll-ttml-db/main/spotify-lyrics/{trackId}.ttml",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                MarkCooldown(ref _amllRetryUntil, TimeSpan.FromMinutes(2));
                return null;
            }
            // A missing community contribution is normal and must not cool down other tracks.
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxLyricsResponseBytes) return null;
            using var body = await PooledUtf8Response.ReadAsync(response, MaxLyricsResponseBytes, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return AmllResult(NativeLyricParser.Parse(body.Span, 0));
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            MarkCooldown(ref _amllRetryUntil, TimeSpan.FromMinutes(2));
            throw;
        }
    }

    internal static LyricsResult? ParseAmllTtml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxLyricsResponseBytes) return null;
        return AmllResult(NativeLyricParser.ParseXml(xml, amll: true));
    }

    private static LyricsResult? AmllResult(List<LyricLine> lines) =>
        lines.Count == 0 ? null : new(lines, LyricsProviderOrder.Amll);
}
