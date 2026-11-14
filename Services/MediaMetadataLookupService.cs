using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public sealed partial class MediaMetadataLookupService : IMediaMetadataLookupService
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_metadata_is_valid_api_key_format(
        ReadOnlySpan<char> key,
        int keyLen,
        out byte outValid);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_metadata_is_direct_video_id(
        ReadOnlySpan<char> title,
        int titleLen,
        out byte outIsVideoId);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_metadata_extract_yt_initial_data_slice(
        ReadOnlySpan<char> html,
        int htmlLen,
        out int outStart,
        out int outLen,
        out byte outFound);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_yt_next_scrape_candidate(
        ReadOnlySpan<char> json,
        int jsonLen,
        int startPos,
        out int outNextPos,
        Span<char> outVideoId,
        int videoIdCap,
        out int outVideoIdLen,
        Span<char> outTitle,
        int titleCap,
        out int outTitleLen,
        Span<char> outChannel,
        int channelCap,
        out int outChannelLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_metadata_extract_youtube_video_id(
        ReadOnlySpan<char> input,
        int inputLen,
        byte mode,
        out int outStart,
        out int outLen,
        out byte outFound);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_metadata_format_spotify_artists(
        ReadOnlySpan<char> rawArtist,
        int rawArtistLen,
        ReadOnlySpan<char> primaryArtist,
        int primaryArtistLen,
        ReadOnlySpan<char> candTitle,
        int candTitleLen,
        ReadOnlySpan<char> expTitle,
        int expTitleLen,
        long candDurMs,
        long expDurMs,
        Span<char> outFormatted,
        int outCap,
        out int outLen,
        out int outScore);

    private const string YouTubeTitleSearchLogTag = "META-YOUTUBE-TITLE-SEARCH";
    private const string YouTubeApiLogTag = "META-YOUTUBE-API";
    private const string ItemsPropertyName = "items";
    private const string VideoIdPropertyName = "videoId";
    private const string TitlePropertyName = "title";
    private const string DurationPropertyName = "duration";

    private static readonly HttpClient SharedHttpClient = new(NetworkPrivacy.Handler(NetworkFeature.Artwork));
    private static readonly HttpClient SharedKeyClient = new(NetworkPrivacy.Handler(NetworkFeature.Artwork));
    private readonly HttpClient _httpClient;
    private readonly HttpClient _keyClient;
    private readonly Func<bool> _artworkAllowed;
    private readonly Func<string?> _apiKey;
    private readonly SpotifyTokenProvider _spotifyTokens;
    private readonly SpotifyQueryMetadataProvider _spotifyQueryMeta;
    private readonly SpotifyCatalogClient _spotifyCatalog;
    private readonly SpotifyLocalMetadataReader? _spotifyLocalMetadata;

    public MediaMetadataLookupService(ISettingsService? settingsService = null)
        : this(SharedHttpClient, SharedKeyClient, IsOnlineArtworkAllowed, () => GetYouTubeApiKey(settingsService),
            new SpotifyLocalMetadataReader())
    {
        _spotifyLocalMetadata?.WarmUp();
        if (_artworkAllowed())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(3000);
                    await _spotifyTokens.GetAccessTokenAsync(null, cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore background token prewarm errors
                }
            });
        }
    }

    internal MediaMetadataLookupService(HttpClient httpClient, HttpClient keyClient, Func<bool> artworkAllowed, Func<string?> apiKey,
        SpotifyLocalMetadataReader? spotifyLocalMetadata = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _keyClient = keyClient ?? throw new ArgumentNullException(nameof(keyClient));
        _artworkAllowed = artworkAllowed ?? throw new ArgumentNullException(nameof(artworkAllowed));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _spotifyTokens = new SpotifyTokenProvider(_httpClient);
        _spotifyQueryMeta = new SpotifyQueryMetadataProvider(_httpClient);
        _spotifyCatalog = new SpotifyCatalogClient(_httpClient, _spotifyQueryMeta);
        _spotifyLocalMetadata = spotifyLocalMetadata;
    }

    internal static HttpRequestMessage CreateYouTubeApiRequest(string url, string apiKey)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme != "https" || uri.Host != "www.googleapis.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith("/youtube/v3/", StringComparison.Ordinal))
            throw new ArgumentException("Invalid YouTube API endpoint.");
        apiKey = apiKey.Trim();
        ReadOnlySpan<char> keySpan = apiKey.AsSpan();
        bool validKey = vn_media_metadata_is_valid_api_key_format(keySpan, keySpan.Length, out byte outValid) == 0
            ? outValid != 0
            : IsPrintableAsciiKey(keySpan);
        if (!validKey)
            throw new ArgumentException("Invalid API key format.");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
    }

    private static bool IsPrintableAsciiKey(ReadOnlySpan<char> keySpan)
    {
        if (keySpan.Length == 0 || keySpan.Length > 4096) return false;
        for (int i = 0; i < keySpan.Length; i++)
        {
            char c = keySpan[i];
            if (c < '!' || c > '~') return false;
        }
        return true;
    }

    private static bool TryExtractYouTubeVideoId(string input, byte mode, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrEmpty(input)) return false;
        ReadOnlySpan<char> span = input.AsSpan();
        if (vn_media_metadata_extract_youtube_video_id(span, span.Length, mode, out int start, out int len, out byte found) == 0 &&
            found != 0 && start >= 0 && len == 11 && start + len <= input.Length)
        {
            videoId = input.Substring(start, len);
            return true;
        }
        return false;
    }

    static MediaMetadataLookupService()
    {
        if (SharedHttpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            SharedHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
    {
        if (!_artworkAllowed())
            return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string cleanTitle = title.Trim();

            if (vn_media_metadata_is_direct_video_id(cleanTitle.AsSpan(), cleanTitle.Length, out byte isDirectId) == 0 && isDirectId != 0)
            {
                var validated = await ResolveVideoIdAsync(cleanTitle, ct);
                if (validated != null)
                    return validated;
            }

            if (TryExtractYouTubeVideoId(cleanTitle, mode: 0, out string extractedId))
            {
                var validated = await ResolveVideoIdAsync(extractedId, ct);
                if (validated != null)
                    return validated;
            }

        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-SEARCH", ex.ToString());
        }

        return null;
    }

    public async Task<YouTubeLookupResult?> TrySearchYouTubeByTitleAsync(string title, string artist = "", CancellationToken ct = default)
    {
        if (!_artworkAllowed() || string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            string query = string.IsNullOrWhiteSpace(artist) || MediaPlatformExtensions.ParsePlatform(artist) == MediaPlatform.YouTube
                ? title
                : $"{title} {artist}";

            string? apiKey = _apiKey();
            if (!string.IsNullOrEmpty(apiKey) && !QuotaCooldown.IsActive(apiKey))
            {
                var apiResult = await TrySearchViaDataApiAsync(query, title, apiKey, ct);
                if (apiResult != null)
                    return apiResult;
            }

            var scrapeResult = await TrySearchViaYouTubeScrapeAsync(query, title, ct);
            if (scrapeResult != null)
                return scrapeResult;

            var pipedResult = await TrySearchViaPipedAsync(query, title, ct);
            if (pipedResult != null)
                return pipedResult;
        }
        catch (OperationCanceledException) { /* Cancelled */ }
        catch (Exception ex)
        {
            RuntimeLog.Error(YouTubeTitleSearchLogTag, ex.ToString());
        }

        return null;
    }

#pragma warning disable S3776
    private async Task<YouTubeLookupResult?> TrySearchViaDataApiAsync(string query, string originalTitle, string apiKey, CancellationToken ct)
    {
        try
        {
            string url = $"https://www.googleapis.com/youtube/v3/search?part=snippet&q={Uri.EscapeDataString(query)}&type=video&maxResults=3&fields=items(id/videoId,snippet(title,channelTitle))";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var request = CreateYouTubeApiRequest(url, apiKey);
            using var response = await _keyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var errorBody = await PooledUtf8Response.ReadAsync(response, YouTubeMetadataParsing.MaxJsonResponseBytes, timeoutCts.Token);
                if (YouTubeMetadataParsing.LooksLikeQuotaExceeded(errorBody.Span))
                    QuotaCooldown.Trip(apiKey);
                return null;
            }

            var candidates = await YouTubeMetadataParsing.ReadMetadataAsync(response, 0, originalTitle, timeoutCts.Token);
            foreach (var candidate in candidates)
            {
                if (candidate.Id is not { Length: > 0 } videoId) continue;
                var enriched = await ResolveVideoIdAsync(videoId, ct);
                if (enriched != null && enriched.TitleMatches(originalTitle)) return enriched;
            }

        }
        catch (OperationCanceledException) { /* Cancelled */ }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-TITLE-SEARCH-API", ex.ToString());
        }

        return null;
    }

#pragma warning disable S3776
    private async Task<YouTubeLookupResult?> TrySearchViaPipedAsync(string query, string originalTitle, CancellationToken ct)
    {
        var pipedInstances = MediaProtocolConfiguration.Current.PipedInstances;

        foreach (var instance in pipedInstances)
        {
            try
            {
                string url = $"https://{instance}/search?q={Uri.EscapeDataString(query)}&filter=videos";

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                var candidates = await YouTubeMetadataParsing.ReadMetadataAsync(response, 1, originalTitle, timeoutCts.Token);
                if (candidates.Count > 0)
                {
                    var candidate = candidates[0];
                    if (candidate.Id is { Length: > 0 } videoId) CacheVideo(videoId, candidate);
                    return candidate;
                }

            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // Ignore instance failure and try next instance
            }
        }

        var invidiousInstances = MediaProtocolConfiguration.Current.InvidiousInstances;

        foreach (var instance in invidiousInstances)
        {
            try
            {
                string url = $"https://{instance}/api/v1/search?q={Uri.EscapeDataString(query)}&type=video";

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                var candidates = await YouTubeMetadataParsing.ReadMetadataAsync(response, 2, originalTitle, timeoutCts.Token);
                if (candidates.Count > 0)
                {
                    var candidate = candidates[0];
                    if (candidate.Id is { Length: > 0 } videoId) CacheVideo(videoId, candidate);
                    return candidate;
                }

            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // Ignore instance failure and try next instance
            }
        }

        return null;
    }

#pragma warning disable S3776
    private async Task<YouTubeLookupResult?> TrySearchViaYouTubeScrapeAsync(string query, string originalTitle, CancellationToken ct)
    {
        try
        {
            string url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Cookie", "CONSENT=PENDING+999");

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            string html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var videoIds = ExtractYouTubeScrapeCandidates(html);
            if (videoIds == null || videoIds.Count == 0)
                return null;

            foreach (var (videoId, videoTitle, channelName) in videoIds)
            {
                var candidate = new YouTubeLookupResult
                {
                    Id = videoId,
                    Title = videoTitle,
                    Author = channelName,
                    Duration = TimeSpan.Zero,
                    ThumbnailUrl = null,
                    Source = YouTubeLookupSource.OEmbed,
                };

                if (candidate.TitleMatches(originalTitle))
                {
                    var enriched = await ResolveVideoIdAsync(videoId, ct);
                    if (enriched != null)
                    {
                        if (!enriched.TitleMatches(originalTitle)) continue;
                        RuntimeLog.Log(YouTubeTitleSearchLogTag,
                            $"yt-scrape-ok query='{query}' videoId={videoId} title='{videoTitle}' duration={enriched.Duration}");
                        CacheVideo(videoId, enriched);
                        return enriched;
                    }
                    RuntimeLog.Log(YouTubeTitleSearchLogTag,
                        $"yt-scrape-ok query='{query}' videoId={videoId} title='{videoTitle}' (no enrich)");
                    CacheVideo(videoId, candidate);
                    return candidate;
                }
            }

        }
        catch (OperationCanceledException) { /* Cancelled */ }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-TITLE-SEARCH-SCRAPE", ex.ToString());
        }

        return null;
    }

    private static List<(string id, string? title, string? channel)>? ExtractYouTubeScrapeCandidates(string html)
    {
        if (vn_media_metadata_extract_yt_initial_data_slice(html.AsSpan(), html.Length, out int jsonStart, out int jsonLen, out byte foundSlice) != 0 || foundSlice == 0)
            return null;

        var videoIds = new List<(string id, string? title, string? channel)>(5);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        ReadOnlySpan<char> jsonSpan = html.AsSpan(jsonStart, jsonLen);
        Span<char> idBuf = stackalloc char[16];
        Span<char> titleBuf = stackalloc char[512];
        Span<char> channelBuf = stackalloc char[256];

        int scanPos = 0;
        while (scanPos < jsonLen && videoIds.Count < 5)
        {
            if (vn_yt_next_scrape_candidate(
                    jsonSpan,
                    jsonLen,
                    scanPos,
                    out int nextPos,
                    idBuf,
                    idBuf.Length,
                    out int idLen,
                    titleBuf,
                    titleBuf.Length,
                    out int titleLen,
                    channelBuf,
                    channelBuf.Length,
                    out int channelLen) != 0 || idLen <= 0)
            {
                break;
            }

            scanPos = nextPos > scanPos ? nextPos : scanPos + 1;
            if (titleLen <= 0)
                continue;

            string videoId = new string(idBuf[..idLen]);
            if (!seenIds.Add(videoId))
                continue;

            string videoTitle = new string(titleBuf[..titleLen]);
            string? channelName = channelLen > 0 ? new string(channelBuf[..channelLen]) : null;
            videoIds.Add((videoId, videoTitle, channelName));
        }

        return videoIds;
    }

    public async Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (!_artworkAllowed())
            return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (!TryExtractYouTubeVideoId(url, mode: 1, out string videoId))
                return null;

            return await ResolveVideoIdAsync(videoId, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-URL", ex.ToString());
        }

        return null;
    }

    private async Task<YouTubeLookupResult?> ResolveVideoIdAsync(string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId) || ct.IsCancellationRequested)
            return null;
        lock (_videoCacheLock) { if (_failedLookups.TryGetValue(videoId, out var failedUntil) && failedUntil > DateTime.UtcNow) return null; }

        string? apiKey = _apiKey();
        if (TryGetCachedVideo(videoId, apiKey, out var cached) && cached != null)
        {
            RuntimeLog.Log(YouTubeApiLogTag,
                $"cache-hit videoId={videoId} source={cached.Source} duration={cached.Duration} thumb={(string.IsNullOrEmpty(cached.ThumbnailUrl) ? "(none)" : "data-api")}");
            return cached;
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            var apiResult = await TryGetVideoFromDataApiAsync(videoId, apiKey, ct);
            if (apiResult != null)
            {
                CacheVideo(videoId, apiResult, apiKey);
                return apiResult;
            }
            if (ct.IsCancellationRequested)
                return null;
            RuntimeLog.Log(YouTubeApiLogTag, $"data-api-miss videoId={videoId} -> falling back to oEmbed");
        }
        else
        {
            RuntimeLog.Log(YouTubeApiLogTag, $"data-api-disabled videoId={videoId} -> using oEmbed only");
        }

        var oembed = await ValidateVideoIdWithOEmbedAsync(videoId, apiKey, ct);
        if (oembed == null)
        {
            if (!ct.IsCancellationRequested)
            {
                lock (_videoCacheLock)
                {
                    if (_failedLookups.Count >= FailedLookupsMaxEntries) _failedLookups.Clear();
                    _failedLookups[videoId] = DateTime.UtcNow.AddMinutes(15);
                }
            }
            return null;
        }

        TimeSpan enrichedDuration = await TryFetchDurationFromDataApiAsync(videoId, apiKey, ct);
        if (enrichedDuration > TimeSpan.Zero)
            oembed = new YouTubeLookupResult { Id = oembed.Id, Title = oembed.Title, Author = oembed.Author, Duration = enrichedDuration, ThumbnailUrl = oembed.ThumbnailUrl, Source = oembed.Source };
        CacheVideo(videoId, oembed, apiKey);
        return oembed;
    }

    public Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default)
        => TryGetSoundCloudArtworkFromUrlAsync(soundCloudUrl, expectedTitle: "", expectedArtist: "", ct);

    public async Task<string?> TryGetSoundCloudArtworkFromUrlAsync(
        string soundCloudUrl,
        string expectedTitle,
        string expectedArtist = "",
        CancellationToken ct = default)
    {
        if (!_artworkAllowed())
            return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            var trackUrl = ExtractSoundCloudTrackUrl(soundCloudUrl);
            if (string.IsNullOrEmpty(trackUrl))
                return null;

            var result = await TryGetSoundCloudOEmbedAsync(trackUrl, ct);
            if (string.IsNullOrWhiteSpace(result.ThumbnailUrl))
                return null;

            if (!string.IsNullOrWhiteSpace(expectedTitle) &&
                !IsSoundCloudOEmbedMatch(expectedTitle, expectedArtist, result.Title, result.Author, candidateScore: 3, strictMode: true))
            {
                return null;
            }

            string normalized = NormalizeSoundCloudArtworkUrl(result.ThumbnailUrl);
            if (!IsLikelySoundCloudPlaceholderArtworkUrl(normalized))
                return normalized;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-SOUNDCLOUD-URL", ex.ToString());
        }

        return null;
    }

    private async Task<YouTubeLookupResult?> ValidateVideoIdWithOEmbedAsync(string videoId, string? apiKey, CancellationToken ct)
    {
        try
        {
            string oembedUrl = $"https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v={videoId}&format=json";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2000);

            using var response = await _httpClient.GetAsync(oembedUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode) return null;
            var results = await YouTubeMetadataParsing.ReadMetadataAsync(response, 3, videoId, timeoutCts.Token);
            return results.Count > 0 ? results[0] : null;
        }
        catch
        {
            return null;
        }
    }

#pragma warning disable S3776
    private async Task<TimeSpan> TryFetchDurationFromDataApiAsync(string videoId, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey) || QuotaCooldown.IsActive(apiKey))
            return TimeSpan.Zero;
        try
        {
            string url = $"https://www.googleapis.com/youtube/v3/videos?part=contentDetails&id={Uri.EscapeDataString(videoId)}&fields=items(contentDetails/duration)";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2000);
            using var request = CreateYouTubeApiRequest(url, apiKey);
            using var response = await _keyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var errorBody = await PooledUtf8Response.ReadAsync(response, YouTubeMetadataParsing.MaxJsonResponseBytes, timeoutCts.Token);
                if (YouTubeMetadataParsing.LooksLikeQuotaExceeded(errorBody.Span)) QuotaCooldown.Trip(apiKey);
                return TimeSpan.Zero;
            }
            var results = await YouTubeMetadataParsing.ReadMetadataAsync(response, 4, videoId, timeoutCts.Token);
            return results.Count > 0 ? results[0].Duration : TimeSpan.Zero;
        }
        catch { /* Best-effort duration enrichment */ }
        return TimeSpan.Zero;
    }

    private async Task<YouTubeLookupResult?> TryGetVideoFromDataApiAsync(string videoId, string apiKey, CancellationToken ct)
    {
        if (QuotaCooldown.IsActive(apiKey))
            return null;

        try
        {
            string url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails&id={Uri.EscapeDataString(videoId)}&fields=items(snippet(title,channelTitle,thumbnails),contentDetails/duration)";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2500);

            using var request = CreateYouTubeApiRequest(url, apiKey);
            using var response = await _keyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var errorBody = await PooledUtf8Response.ReadAsync(response, YouTubeMetadataParsing.MaxJsonResponseBytes, timeoutCts.Token);
                if (YouTubeMetadataParsing.LooksLikeQuotaExceeded(errorBody.Span)) QuotaCooldown.Trip(apiKey);
                return null;
            }
            var results = await YouTubeMetadataParsing.ReadMetadataAsync(response, 4, videoId, timeoutCts.Token);
            return results.Count > 0 ? results[0] : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(YouTubeApiLogTag, ex.ToString());
            return null;
        }
    }
    private static string? PickBestThumbnail(JsonElement thumbnails)
        => YouTubeMetadataParsing.PickBestThumbnail(thumbnails);

    private static bool LooksLikeQuotaExceeded(string body)
        => YouTubeMetadataParsing.LooksLikeQuotaExceeded(body);

    private static readonly YouTubeQuotaCooldown QuotaCooldown = new();

    private static readonly object _videoCacheLock = new();
    private static readonly Dictionary<string, (YouTubeLookupResult Result, DateTime ExpiresUtc)> _videoCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> _failedLookups = new(StringComparer.Ordinal);
    private const int FailedLookupsMaxEntries = 500;
    private static readonly TimeSpan _videoCacheTtl = TimeSpan.FromHours(6);
    private const int VideoCacheMaxEntries = 256;

    private static string VideoCacheKey(string videoId, string? apiKey) => videoId + ":" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(apiKey?.Trim() ?? "")));

    private static bool TryGetCachedVideo(string videoId, string? apiKey, out YouTubeLookupResult? result)
    {
        lock (_videoCacheLock)
        {
            if (_videoCache.TryGetValue(VideoCacheKey(videoId, apiKey), out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
            {
                result = entry.Result;
                return true;
            }
        }
        result = null;
        return false;
    }

    private static void CacheVideo(string videoId, YouTubeLookupResult result, string? apiKey = null)
    {
        if (string.IsNullOrEmpty(videoId)) return;
        lock (_videoCacheLock)
        {
            if (_videoCache.Count >= VideoCacheMaxEntries)
            {
                var now = DateTime.UtcNow;
                List<string>? stale = null;
                foreach (var kv in _videoCache)
                {
                    if (kv.Value.ExpiresUtc <= now)
                    {
                        stale ??= new List<string>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                        _videoCache.Remove(stale[i]);
                }
                if (_videoCache.Count >= VideoCacheMaxEntries)
                {
                    int evictCount = _videoCache.Count / 2;
                    var toEvict = new List<string>(evictCount);
                    foreach (var key in _videoCache.Keys)
                    {
                        if (toEvict.Count >= evictCount) break;
                        toEvict.Add(key);
                    }
                    for (int i = 0; i < toEvict.Count; i++)
                        _videoCache.Remove(toEvict[i]);
                }
            }
            _videoCache[VideoCacheKey(videoId, apiKey)] = (result, DateTime.UtcNow.Add(_videoCacheTtl));
        }
    }

    private static TimeSpan ParseIso8601Duration(string iso)
        => YouTubeMetadataParsing.ParseIso8601Duration(iso);

    internal static string? GetYouTubeApiKey(ISettingsService? settingsService)
    {
        if (settingsService == null)
            return null;
        try
        {
            var settings = settingsService.Load();
            if (!settings.EnableYouTubeApi)
                return null;
            string candidate = (settings.YouTubeApiKey ?? "").Trim();
            return candidate.Length > 10 ? candidate : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("YOUTUBE-API-KEY", $"Failed to read API key from settings: {ex.Message}");
            return null;
        }
    }

    public static bool IsOnlineArtworkAllowed()
    {
        try
        {
            // The policy tracks the live settings (local-only mode and the artwork toggle)
            // and denies until startup has loaded them.
            return NetworkPrivacy.Current.IsAllowed(NetworkFeature.Artwork);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("ARTWORK-PRIVACY", $"Privacy check failed, denying: {ex.Message}");
            return false;
        }
    }

#pragma warning disable S3776
    public async Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
    {
        if (!_artworkAllowed())
            return null;

        try
        {
            var directTrackUrl = ExtractSoundCloudTrackUrl(title);
            if (!string.IsNullOrEmpty(directTrackUrl))
            {
                var direct = await TryGetSoundCloudOEmbedAsync(directTrackUrl, ct);
                if (!string.IsNullOrWhiteSpace(direct.ThumbnailUrl))
                {
                    string normalizedDirectThumbnail = NormalizeSoundCloudArtworkUrl(direct.ThumbnailUrl);
                    if (!IsLikelySoundCloudPlaceholderArtworkUrl(normalizedDirectThumbnail))
                    {
                        return normalizedDirectThumbnail;
                    }
                }
            }

            string cleanedTitle = SanitizeSearchText(title);
            string cleanedArtist = SanitizeSearchText(artist);
            string query = cleanedTitle;
            if (!string.IsNullOrWhiteSpace(cleanedArtist) &&
                MediaPlatformExtensions.ParsePlatform(artist) is not (MediaPlatform.SoundCloud or MediaPlatform.Browser) &&
                !cleanedTitle.Contains(cleanedArtist, StringComparison.OrdinalIgnoreCase))
            {
                query = $"{cleanedTitle} {cleanedArtist}".Trim();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var combinedScores = new Dictionary<string, (int Score, int FirstIndex)>(StringComparer.OrdinalIgnoreCase);
            string escapedQuery = Uri.EscapeDataString(query);
            var searchHtmls = await Task.WhenAll(
                GetStringWithTimeoutAsync($"https://m.soundcloud.com/search/sounds?q={escapedQuery}", 1200, ct),
                GetStringWithTimeoutAsync($"https://soundcloud.com/search/sounds?q={escapedQuery}", 1200, ct));

            int nextIndex = 0;
            foreach (var html in searchHtmls)
            {
                if (string.IsNullOrWhiteSpace(html))
                    continue;

                var found = ExtractSoundCloudTrackUrlsFromSearchHtml(html, title, artist);
                foreach (var candidate in found)
                {
                    if (combinedScores.TryGetValue(candidate.Url, out var existing))
                    {
                        if (candidate.Score > existing.Score)
                            combinedScores[candidate.Url] = (candidate.Score, existing.FirstIndex);
                    }
                    else
                    {
                        combinedScores[candidate.Url] = (candidate.Score, nextIndex++);
                    }
                }
            }

            if (combinedScores.Count == 0)
                return null;

            var candidates = new List<(string Url, int Score, int FirstIndex)>(combinedScores.Count);
            foreach (var item in combinedScores)
                candidates.Add((item.Key, item.Value.Score, item.Value.FirstIndex));

            candidates.Sort(static (a, b) =>
            {
                int cmp = b.Score.CompareTo(a.Score);
                return cmp != 0 ? cmp : a.FirstIndex.CompareTo(b.FirstIndex);
            });

            int probeCount = Math.Min(3, candidates.Count);
            var probeTasks = new Task<SoundCloudCandidateProbe>[probeCount];
            for (int i = 0; i < probeCount; i++)
            {
                var candidate = candidates[i];
                probeTasks[i] = ProbeSoundCloudCandidateAsync(
                    candidate.Url,
                    candidate.Score,
                    i,
                    title,
                    artist,
                    requireStrongMatch,
                    ct);
            }

            var probes = await Task.WhenAll(probeTasks);
            Array.Sort(probes, static (a, b) => a.Index.CompareTo(b.Index));

            for (int i = 0; i < probes.Length; i++)
            {
                var probe = probes[i];
                if (probe.IsMatch && !string.IsNullOrWhiteSpace(probe.ThumbnailUrl))
                    return probe.ThumbnailUrl;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct SoundCloudCandidateProbe(int Index, string? ThumbnailUrl, bool IsMatch);

    private async Task<SoundCloudCandidateProbe> ProbeSoundCloudCandidateAsync(
        string url,
        int candidateScore,
        int index,
        string expectedTitle,
        string expectedArtist,
        bool requireStrongMatch,
        CancellationToken ct)
    {
        try
        {
            var result = await TryGetSoundCloudOEmbedAsync(url, ct);
            if (string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                return new SoundCloudCandidateProbe(index, null, false);
            }

            string normalizedThumb = NormalizeSoundCloudArtworkUrl(result.ThumbnailUrl);
            if (IsLikelySoundCloudPlaceholderArtworkUrl(normalizedThumb))
            {
                return new SoundCloudCandidateProbe(index, null, false);
            }

            bool isMatch = IsSoundCloudOEmbedMatch(
                expectedTitle,
                expectedArtist,
                result.Title,
                result.Author,
                candidateScore,
                requireStrongMatch);

            return new SoundCloudCandidateProbe(index, normalizedThumb, isMatch);
        }
        catch
        {
            return new SoundCloudCandidateProbe(index, null, false);
        }
    }

    private async Task<string?> GetStringWithTimeoutAsync(string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            return await _httpClient.GetStringAsync(url, timeoutCts.Token);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSoundCloudTrackUrl(string rawText)
        => SoundCloudMatching.ExtractTrackUrl(rawText);

    private static string SanitizeSearchText(string value)
        => SoundCloudMatching.SanitizeSearchText(value);

    private static List<(string Url, int Score)> ExtractSoundCloudTrackUrlsFromSearchHtml(string html, string title, string artist)
        => SoundCloudMatching.ExtractTrackUrlsFromSearchHtml(html, title, artist);

    private static bool IsSoundCloudOEmbedMatch(string expectedTitle, string expectedArtist, string? candidateTitle, string? candidateAuthor, int candidateScore, bool strictMode = false)
        => SoundCloudMatching.IsOEmbedMatch(expectedTitle, expectedArtist, candidateTitle, candidateAuthor, candidateScore, strictMode);

    private static string NormalizeSoundCloudArtworkUrl(string url)
        => SoundCloudMatching.NormalizeArtworkUrl(url);

    private async Task<(string? ThumbnailUrl, string? Title, string? Author)> TryGetSoundCloudOEmbedAsync(string trackUrl, CancellationToken ct)
    {
        try
        {
            string requestUrl = $"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(trackUrl)}";
            string? json = await GetStringWithTimeoutAsync(requestUrl, 1100, ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (null, null, null);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? thumbnailUrl = root.TryGetProperty("thumbnail_url", out var thumbnailEl) ? thumbnailEl.GetString() : null;
            string? oembedTitle = root.TryGetProperty(TitlePropertyName, out var titleEl) ? titleEl.GetString() : null;
            string? oembedAuthor = root.TryGetProperty("author_name", out var authorEl) ? authorEl.GetString() : null;
            return (thumbnailUrl, oembedTitle, oembedAuthor);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
        => MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(url);

    internal static string? FormatSpotifyArtistsCandidate(
        string? candidateTitle,
        string? candidateArtists,
        long candidateDurationMs,
        string targetTitle,
        string primaryArtist,
        long targetDurationMs,
        out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(candidateTitle) || string.IsNullOrWhiteSpace(candidateArtists))
            return null;

        Span<char> buf = stackalloc char[512];
        if (vn_media_metadata_format_spotify_artists(
                candidateArtists.AsSpan(), candidateArtists.Length,
                primaryArtist.AsSpan(), primaryArtist.Length,
                candidateTitle.AsSpan(), candidateTitle.Length,
                targetTitle.AsSpan(), targetTitle.Length,
                candidateDurationMs,
                targetDurationMs,
                buf, buf.Length,
                out int outLen,
                out score) == 0 && outLen > 0 && score > 0)
        {
            return new string(buf[..outLen]);
        }

        return null;
    }

    public async Task<string?> TryGetSpotifyFullArtistsAsync(
        string title,
        string primaryArtist,
        TimeSpan duration = default,
        string? albumTitle = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(primaryArtist))
            return null;

        string cleanTitle = title.Trim();
        string cleanArtist = primaryArtist.Trim();
        string? cleanAlbum = string.IsNullOrWhiteSpace(albumTitle) ? null : albumTitle.Trim();
        long targetDurationMs = duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : 0;

        try
        {
            if (_spotifyLocalMetadata != null)
            {
                string? localArtists = await _spotifyLocalMetadata.TryGetArtistsAsync(cleanTitle, cleanArtist, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(localArtists))
                    return localArtists;
            }
            if (!_artworkAllowed()) return null;

            string? spotifyMatch = await TrySearchSpotifyCatalogArtistsAsync(cleanTitle, cleanArtist, cleanAlbum, ct);
            if (!string.IsNullOrEmpty(spotifyMatch))
                return spotifyMatch;

            string? itunesMatch = await TrySearchItunesArtistsAsync(cleanTitle, cleanArtist, cleanAlbum, targetDurationMs, "VN", ct)
                               ?? await TrySearchItunesArtistsAsync(cleanTitle, cleanArtist, cleanAlbum, targetDurationMs, null, ct);
            if (!string.IsNullOrEmpty(itunesMatch))
                return itunesMatch;

            return await TrySearchDeezerArtistsAsync(cleanTitle, cleanArtist, cleanAlbum, targetDurationMs, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> TryGetSpotifyArtworkUrlAsync(
        string title,
        string primaryArtist,
        TimeSpan duration = default,
        string? albumTitle = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(primaryArtist) || !_artworkAllowed())
            return null;

        string cleanTitle = title.Trim();
        string cleanArtist = primaryArtist.Trim();
        string? cleanAlbum = string.IsNullOrWhiteSpace(albumTitle) ? null : albumTitle.Trim();
        long targetDurationMs = duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : 0;

        try
        {
            string? spotifyArt = await TrySearchSpotifyCatalogArtworkAsync(cleanTitle, cleanArtist, cleanAlbum, ct);
            if (!string.IsNullOrEmpty(spotifyArt))
                return spotifyArt;

            string? itunesArt = await TrySearchItunesArtworkAsync(cleanTitle, cleanArtist, cleanAlbum, targetDurationMs, "VN", ct)
                             ?? await TrySearchItunesArtworkAsync(cleanTitle, cleanArtist, cleanAlbum, targetDurationMs, null, ct);
            if (!string.IsNullOrEmpty(itunesArt))
                return itunesArt;

            return await TrySearchDeezerArtworkAsync(cleanTitle, cleanArtist, cleanAlbum, targetDurationMs, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TrySearchSpotifyCatalogArtworkAsync(
        string title,
        string primaryArtist,
        string? albumTitle,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2500);
            string? token = await _spotifyTokens.GetAccessTokenAsync(null, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                return null;

            return await _spotifyCatalog.ResolveTrackArtworkUrlAsync(title, primaryArtist, albumTitle, token, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TrySearchItunesArtworkAsync(
        string title,
        string primaryArtist,
        string? albumTitle,
        long targetDurationMs,
        string? country,
        CancellationToken ct)
    {
        string query = Uri.EscapeDataString($"{title} {primaryArtist}");
        string url = string.IsNullOrEmpty(country)
            ? $"https://itunes.apple.com/search?term={query}&entity=song&limit=5"
            : $"https://itunes.apple.com/search?term={query}&entity=song&limit=5&country={country}";

        using var body = await GetCatalogBodyAsync(url, 2200, ct).ConfigureAwait(false);
        return body == null ? null : NativeCatalogParser.Select(body.Span, 2, title, primaryArtist, targetDurationMs, albumTitle).Key;
    }

    private async Task<string?> TrySearchDeezerArtworkAsync(
        string title,
        string primaryArtist,
        string? albumTitle,
        long targetDurationMs,
        CancellationToken ct)
    {
        try
        {
            string query = Uri.EscapeDataString($"{title} {primaryArtist}");
            string searchUrl = $"https://api.deezer.com/search?q={query}&limit=5";
            using var searchBody = await GetCatalogBodyAsync(searchUrl, 2200, ct).ConfigureAwait(false);
            if (searchBody == null) return null;
            var tracks = YouTubeMetadataParsing.ParseUtf8(searchBody.Span, 7).Items;
            string? bestArtwork = null;
            int bestScore = 0;
            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();
                using var detail = await GetCatalogBodyAsync($"https://api.deezer.com/track/{track.Ticks}", 2000, ct).ConfigureAwait(false);
                if (detail == null) continue;
                var selected = NativeCatalogParser.Select(detail.Span, 6, title, primaryArtist, targetDurationMs, albumTitle, searchBody.Span, track.Ticks);
                if (!string.IsNullOrEmpty(selected.Key) && selected.Score > bestScore) { bestArtwork = selected.Key; bestScore = selected.Score; }
            }
            return bestArtwork;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TrySearchSpotifyCatalogArtistsAsync(
        string title,
        string primaryArtist,
        string? albumTitle,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2500);
            string? token = await _spotifyTokens.GetAccessTokenAsync(null, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                return null;

            return await _spotifyCatalog.ResolveTrackArtistsAsync(title, primaryArtist, albumTitle, token, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static int ComputeAlbumTieBreakerBonus(string? candidateAlbum, string? expectedAlbum)
    {
        if (string.IsNullOrWhiteSpace(expectedAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
            return 0;

        string normCand = SpotifyTrackMatcher.NormalizeForMatch(candidateAlbum);
        string normExp = SpotifyTrackMatcher.NormalizeForMatch(expectedAlbum);
        if (normCand.Length == 0 || normExp.Length == 0)
            return 0;

        if (string.Equals(normCand, normExp, StringComparison.Ordinal) ||
            normCand.StartsWith(normExp + " ", StringComparison.Ordinal))
        {
            return 40;
        }

        return -20;
    }

    private async Task<string?> TrySearchItunesArtistsAsync(
        string title,
        string primaryArtist,
        string? albumTitle,
        long targetDurationMs,
        string? country,
        CancellationToken ct)
    {
        string query = Uri.EscapeDataString($"{title} {primaryArtist}");
        string url = string.IsNullOrEmpty(country)
            ? $"https://itunes.apple.com/search?term={query}&entity=song&limit=5"
            : $"https://itunes.apple.com/search?term={query}&entity=song&limit=5&country={country}";

        using var body = await GetCatalogBodyAsync(url, 2200, ct).ConfigureAwait(false);
        return body == null ? null : NativeCatalogParser.Select(body.Span, 2, title, primaryArtist, targetDurationMs, albumTitle).Text;
    }

    private async Task<PooledUtf8Response?> GetCatalogBodyAsync(string url, int timeoutMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await PooledUtf8Response.ReadAsync(response, YouTubeMetadataParsing.MaxJsonResponseBytes, timeout.Token).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task<string?> TrySearchDeezerArtistsAsync(
        string title,
        string primaryArtist,
        string? albumTitle,
        long targetDurationMs,
        CancellationToken ct)
    {
        string query = Uri.EscapeDataString($"{title} {primaryArtist}");
        string searchUrl = $"https://api.deezer.com/search?q={query}&limit=5";
        using var searchBody = await GetCatalogBodyAsync(searchUrl, 2200, ct).ConfigureAwait(false);
        if (searchBody == null) return null;
        var tracks = YouTubeMetadataParsing.ParseUtf8(searchBody.Span, 7).Items;
        string? bestArtists = null;
        int bestScore = 0;
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            using var detail = await GetCatalogBodyAsync($"https://api.deezer.com/track/{track.Ticks}", 2000, ct).ConfigureAwait(false);
            if (detail == null) continue;
            var selected = NativeCatalogParser.Select(detail.Span, 6, title, primaryArtist, targetDurationMs, albumTitle, searchBody.Span, track.Ticks);
            if (selected.Text != null && selected.Score > bestScore) { bestArtists = selected.Text; bestScore = selected.Score; }
        }
        return bestArtists;
    }
}
