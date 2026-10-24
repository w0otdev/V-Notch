using System.Net.Http;
using System.Runtime.InteropServices;
using static VNotch.Services.SpotifyHttpResponseReader;

namespace VNotch.Services;

internal enum SpotifyQuery { FindTracks, Canvas }

internal sealed partial class SpotifyQueryMetadataProvider : IDisposable
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_query_meta_find_script_url(
        ReadOnlySpan<char> htmlPtr,
        int htmlLen,
        byte preferMobile,
        out byte outFound,
        out int outStart,
        out int outLen);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotify_query_meta_contains_not_found(
        ReadOnlySpan<char> jsonPtr,
        int jsonLen,
        out byte outContains);

    private const string LogCategory = "SPOTIFY-CANVAS";
    private const int MaxWebPlayerScriptBytes = 8 * 1024 * 1024;
    private const int MaxCachedScripts = 4;
    private const string DesktopWebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";
    private const string MobileWebUserAgent =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Mobile Safari/537.36";
    private static readonly Uri SpotifyWebUri = new("https://open.spotify.com/");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    // Content-addressed bundle URLs are immutable. Retain only their two hashes.
    private readonly Dictionary<Uri, SpotifyScriptMetadataReader.QueryHashes> _scripts = new();
    private volatile string _findTracksHash = MediaProtocolConfiguration.Current.SpotifyFindTracksHash;
    private volatile string _canvasHash = MediaProtocolConfiguration.Current.SpotifyCanvasHash;

    internal SpotifyQueryMetadataProvider(HttpClient http) => _http = http;
    internal string FindTracksHash => _findTracksHash;
    internal string CanvasHash => _canvasHash;

    internal async Task<string?> RefreshAsync(SpotifyQuery query, string failedHash, CancellationToken token)
    {
        await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string currentHash = query == SpotifyQuery.FindTracks ? FindTracksHash : CanvasHash;
            if (!currentHash.Equals(failedHash, StringComparison.Ordinal))
                return currentHash;

            string userAgent = query == SpotifyQuery.FindTracks ? MobileWebUserAgent : DesktopWebUserAgent;
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, SpotifyWebUri);
            pageRequest.Headers.UserAgent.ParseAdd(userAgent);
            string? html = await SendForStringAsync(_http, pageRequest, token).ConfigureAwait(false);
            if (html == null)
                return null;

            byte preferMobile = query == SpotifyQuery.FindTracks ? (byte)1 : (byte)0;
            if (vn_spotify_query_meta_find_script_url(html.AsSpan(), html.Length, preferMobile, out byte found, out int start, out int len) != 0 ||
                found == 0 ||
                !Uri.TryCreate(html.Substring(start, len), UriKind.Absolute, out var scriptUri) ||
                scriptUri.Scheme != Uri.UriSchemeHttps ||
                !scriptUri.Host.Equals("open.spotifycdn.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!_scripts.TryGetValue(scriptUri, out var hashes))
            {
                hashes = await ReadScriptAsync(scriptUri, userAgent, token).ConfigureAwait(false);
                if (hashes == null)
                    return null;
                if (_scripts.Count >= MaxCachedScripts)
                    _scripts.Remove(_scripts.Keys.First());
                _scripts.Add(scriptUri, hashes);
            }

            if (hashes.FindTracksHash != null)
                _findTracksHash = hashes.FindTracksHash;
            if (hashes.CanvasHash != null)
                _canvasHash = hashes.CanvasHash;
            string? refreshed = query == SpotifyQuery.FindTracks ? hashes.FindTracksHash : hashes.CanvasHash;
            if (refreshed != null)
                RuntimeLog.Debug(LogCategory, "Refreshed Spotify query metadata");
            return refreshed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.Debug(LogCategory, () => $"Unable to refresh Spotify query metadata: {ex.Message}");
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<SpotifyScriptMetadataReader.QueryHashes?> ReadScriptAsync(
        Uri scriptUri, string userAgent, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, scriptUri);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxWebPlayerScriptBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await SpotifyScriptMetadataReader.ReadMetadataAsync(
            stream, MaxWebPlayerScriptBytes, token).ConfigureAwait(false);
    }

    internal static bool ContainsPersistedQueryNotFound(string json) =>
        json != null &&
        vn_spotify_query_meta_contains_not_found(json.AsSpan(), json.Length, out byte contains) == 0 &&
        contains != 0;

    public void Dispose() => _refreshLock.Dispose();
}
