using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private const string YouTubeTitleSearchLogTag = "META-YOUTUBE-TITLE-SEARCH";
    private const string YouTubeApiLogTag = "META-YOUTUBE-API";
    private const string ItemsPropertyName = "items";
    private const string VideoIdPropertyName = "videoId";
    private const string TitlePropertyName = "title";
    private const string DurationPropertyName = "duration";

    private static readonly HttpClient SharedHttpClient = new(NetworkPrivacy.Handler(NetworkFeature.Artwork));
    private static readonly HttpClient SharedKeyClient = new(NetworkPrivacy.Handler(NetworkFeature.Artwork, new NativeWinHttpHandler { AllowAutoRedirect = false, UseCookies = false }));
    private readonly HttpClient _httpClient;
    private readonly HttpClient _keyClient;
    private readonly Func<bool> _artworkAllowed;
    private readonly Func<string?> _apiKey;

    public MediaMetadataLookupService() : this(SharedHttpClient, SharedKeyClient, IsOnlineArtworkAllowed, GetYouTubeApiKey) { }

    internal MediaMetadataLookupService(HttpClient httpClient, HttpClient keyClient, Func<bool> artworkAllowed, Func<string?> apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _keyClient = keyClient ?? throw new ArgumentNullException(nameof(keyClient));
        _artworkAllowed = artworkAllowed ?? throw new ArgumentNullException(nameof(artworkAllowed));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
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
            : (apiKey.Length > 0 && apiKey.Length <= 4096 && !apiKey.Any(c => c < '!' || c > '~'));
        if (!validKey)
            throw new ArgumentException("Invalid API key format.");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-goog-api-key", apiKey);
        return request;
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

            var urlMatch = Regex.Match(cleanTitle, @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            if (urlMatch.Success)
            {
                string extractedId = urlMatch.Groups[1].Value;
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
            using var response = await _keyClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (LooksLikeQuotaExceeded(body))
                    QuotaCooldown.Trip(apiKey);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty(ItemsPropertyName, out var items) || items.GetArrayLength() == 0)
                return null;

            foreach (var item in items.EnumerateArray())
            {
                string? videoId = null;
                string? resultTitle = null;
                string? channelTitle = null;

                if (item.TryGetProperty("id", out var idEl) && idEl.TryGetProperty(VideoIdPropertyName, out var vidEl))
                    videoId = vidEl.GetString();
                if (item.TryGetProperty("snippet", out var snippet))
                {
                    if (snippet.TryGetProperty(TitlePropertyName, out var titleEl))
                        resultTitle = titleEl.GetString();
                    if (snippet.TryGetProperty("channelTitle", out var chEl))
                        channelTitle = chEl.GetString();
                }

                if (string.IsNullOrEmpty(videoId))
                    continue;

                var candidate = new YouTubeLookupResult
                {
                    Id = videoId,
                    Title = resultTitle,
                    Author = channelTitle,
                    Duration = TimeSpan.Zero,
                    ThumbnailUrl = null,
                    Source = YouTubeLookupSource.DataApi,
                };

                if (candidate.TitleMatches(originalTitle))
                {
                    var enriched = await ResolveVideoIdAsync(videoId, ct);
                    if (enriched != null && enriched.TitleMatches(originalTitle))
                    {
                        RuntimeLog.Log(YouTubeTitleSearchLogTag,
                            $"data-api-search-ok query='{query}' videoId={videoId} title='{resultTitle}'");
                        return enriched;
                    }
                }
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

                using var response = await _httpClient.GetAsync(url, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty(ItemsPropertyName, out var items) || items.GetArrayLength() == 0)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    string? itemUrl = null;
                    string? itemTitle = null;
                    string? uploaderName = null;
                    long durationSec = 0;
                    string? thumbnailUrl = null;

                    if (item.TryGetProperty("url", out var urlEl))
                        itemUrl = urlEl.GetString();
                    if (item.TryGetProperty(TitlePropertyName, out var titleEl))
                        itemTitle = titleEl.GetString();
                    if (item.TryGetProperty("uploaderName", out var uploaderEl))
                        uploaderName = uploaderEl.GetString();
                    if (item.TryGetProperty(DurationPropertyName, out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                        durationSec = durEl.GetInt64();
                    if (item.TryGetProperty("thumbnail", out var thumbEl))
                        thumbnailUrl = thumbEl.GetString();

                    if (string.IsNullOrEmpty(itemUrl))
                        continue;

                    var match = Regex.Match(itemUrl, @"v=([a-zA-Z0-9_-]{11})", RegexOptions.None, TimeSpan.FromMilliseconds(250));
                    if (!match.Success)
                        continue;

                    string videoId = match.Groups[1].Value;

                    var candidate = new YouTubeLookupResult
                    {
                        Id = videoId,
                        Title = itemTitle,
                        Author = uploaderName,
                        Duration = TimeSpan.FromSeconds(durationSec),
                        ThumbnailUrl = thumbnailUrl,
                        Source = YouTubeLookupSource.OEmbed,
                    };

                    if (candidate.TitleMatches(originalTitle))
                    {
                        RuntimeLog.Log(YouTubeTitleSearchLogTag,
                            $"piped-search-ok instance={instance} query='{query}' videoId={videoId} title='{itemTitle}'");
                        CacheVideo(videoId, candidate);
                        return candidate;
                    }
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

                using var response = await _httpClient.GetAsync(url, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    continue;

                foreach (var item in root.EnumerateArray())
                {
                    string? videoId = item.TryGetProperty(VideoIdPropertyName, out var vidEl) ? vidEl.GetString() : null;
                    string? itemTitle = item.TryGetProperty(TitlePropertyName, out var titleEl) ? titleEl.GetString() : null;
                    string? author = item.TryGetProperty("author", out var authEl) ? authEl.GetString() : null;
                    long lengthSec = item.TryGetProperty("lengthSeconds", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number ? lenEl.GetInt64() : 0;

                    if (string.IsNullOrEmpty(videoId))
                        continue;

                    var candidate = new YouTubeLookupResult
                    {
                        Id = videoId,
                        Title = itemTitle,
                        Author = author,
                        Duration = TimeSpan.FromSeconds(lengthSec),
                        ThumbnailUrl = null,
                        Source = YouTubeLookupSource.OEmbed,
                    };

                    if (candidate.TitleMatches(originalTitle))
                    {
                        RuntimeLog.Log(YouTubeTitleSearchLogTag,
                            $"invidious-search-ok instance={instance} query='{query}' videoId={videoId} title='{itemTitle}'");
                        CacheVideo(videoId, candidate);
                        return candidate;
                    }
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

            var match = Regex.Match(url, @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=|youtube\.com/embed/|youtube\.com/v/)([a-zA-Z0-9_-]{11})", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            if (!match.Success)
                return null;

            string videoId = match.Groups[1].Value;
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

        var oembed = await ValidateVideoIdWithOEmbedAsync(videoId, ct);
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

    private async Task<YouTubeLookupResult?> ValidateVideoIdWithOEmbedAsync(string videoId, CancellationToken ct)
    {
        try
        {
            string oembedUrl = $"https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v={videoId}&format=json";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2000);

            string json = await _httpClient.GetStringAsync(oembedUrl, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? oembedTitle = root.TryGetProperty(TitlePropertyName, out var titleEl) ? titleEl.GetString() : null;
            string? oembedAuthor = root.TryGetProperty("author_name", out var authorEl) ? authorEl.GetString() : null;

            return new YouTubeLookupResult
            {
                Id = videoId,
                Title = oembedTitle,
                Author = oembedAuthor,
                Duration = TimeSpan.Zero,
                ThumbnailUrl = null,
                Source = YouTubeLookupSource.OEmbed,
            };
        }
        catch
        {
            return null;
        }
    }

#pragma warning disable S3776
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
            using var response = await _keyClient.SendAsync(request, timeoutCts.Token);
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (LooksLikeQuotaExceeded(json))
                {
                    QuotaCooldown.Trip(apiKey);
                    RuntimeLog.Log(YouTubeApiLogTag, "quotaExceeded — Data API disabled until tomorrow, falling back to oEmbed.");
                }
                else
                {
                    RuntimeLog.Log(YouTubeApiLogTag, $"HTTP {(int)response.StatusCode} for videoId={videoId}");
                }
                return null;
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(ItemsPropertyName, out var items) || items.GetArrayLength() == 0)
                return null;

            var item = items[0];
            string? title = null;
            string? channel = null;
            string? thumbnailUrl = null;
            TimeSpan duration = TimeSpan.Zero;

            if (item.TryGetProperty("snippet", out var snippet))
            {
                if (snippet.TryGetProperty(TitlePropertyName, out var titleEl)) title = titleEl.GetString();
                if (snippet.TryGetProperty("channelTitle", out var channelEl)) channel = channelEl.GetString();
                if (snippet.TryGetProperty("thumbnails", out var thumbs))
                    thumbnailUrl = PickBestThumbnail(thumbs);
            }

            if (item.TryGetProperty("contentDetails", out var details) &&
                details.TryGetProperty(DurationPropertyName, out var durationEl))
            {
                string? iso = durationEl.GetString();
                if (!string.IsNullOrEmpty(iso))
                    duration = ParseIso8601Duration(iso);
            }

            RuntimeLog.Log(YouTubeApiLogTag,
                $"data-api-ok videoId={videoId} title='{title}' channel='{channel}' duration={duration} thumb={(string.IsNullOrWhiteSpace(thumbnailUrl) ? "(none)" : "ok")}");

            return new YouTubeLookupResult
            {
                Id = videoId,
                Title = title,
                Author = channel,
                Duration = duration,
                ThumbnailUrl = thumbnailUrl,
                Source = YouTubeLookupSource.DataApi,
            };
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
                var stale = _videoCache.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList();
                foreach (var key in stale) _videoCache.Remove(key);
                if (_videoCache.Count >= VideoCacheMaxEntries)
                {
                    foreach (var key in _videoCache.Keys.Take(_videoCache.Count / 2).ToList())
                        _videoCache.Remove(key);
                }
            }
            _videoCache[VideoCacheKey(videoId, apiKey)] = (result, DateTime.UtcNow.Add(_videoCacheTtl));
        }
    }

    private static TimeSpan ParseIso8601Duration(string iso)
        => YouTubeMetadataParsing.ParseIso8601Duration(iso);

    private static readonly object ApiKeyCacheGate = new();
    private static string? _apiKeyCachePath;
    private static DateTime _apiKeyCacheStampUtc;
    private static string? _cachedApiKey;

    private static string? GetYouTubeApiKey()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return GetCachedYouTubeApiKey(Path.Combine(appDataPath, "V-Notch", "settings.json"));
    }

    // Settings are saved with an atomic replace, so the write time changes whenever the
    // key or its toggle can change. Re-read and decrypt only then, not on every lookup.
    internal static string? GetCachedYouTubeApiKey(string settingsPath)
    {
        DateTime stampUtc;
        try
        {
            stampUtc = File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : DateTime.MinValue;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("YOUTUBE-API-KEY", $"Failed to stat settings: {ex.Message}");
            return null;
        }

        lock (ApiKeyCacheGate)
        {
            if (_apiKeyCachePath == settingsPath && _apiKeyCacheStampUtc == stampUtc)
                return _cachedApiKey;
        }

        // Read errors are not cached so a transient failure does not hide the key.
        if (!TryReadYouTubeApiKey(settingsPath, out var key))
            return null;

        lock (ApiKeyCacheGate)
        {
            _apiKeyCachePath = settingsPath;
            _apiKeyCacheStampUtc = stampUtc;
            _cachedApiKey = key;
        }
        return key;
    }

    private static bool TryReadYouTubeApiKey(string settingsPath, out string? key)
    {
        key = null;
        try
        {
            if (!File.Exists(settingsPath))
                return true;

            string json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("EnableYouTubeApi", out var enabledEl) &&
                enabledEl.GetBoolean() &&
                root.TryGetProperty("YouTubeApiKey", out var keyEl))
            {
                string? candidate = DataProtection.Unprotect(keyEl.GetString())?.Trim();
                if (!string.IsNullOrEmpty(candidate) && candidate.Length > 10)
                    key = candidate;
            }
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("YOUTUBE-API-KEY", $"Failed to read API key from settings: {ex.Message}");
            return false;
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
            string[] searchUrls =
            {
                $"https://m.soundcloud.com/search/sounds?q={Uri.EscapeDataString(query)}",
                $"https://soundcloud.com/search/sounds?q={Uri.EscapeDataString(query)}"
            };

            var searchTasks = new List<Task<string?>>(searchUrls.Length);
            foreach (var searchUrl in searchUrls)
            {
                searchTasks.Add(GetStringWithTimeoutAsync(searchUrl, 1200, ct));
            }

            var searchHtmls = await Task.WhenAll(searchTasks);
            int nextIndex = 0;
            foreach (var html in searchHtmls)
            {
                if (string.IsNullOrWhiteSpace(html))
                {
                    continue;
                }

                var found = ExtractSoundCloudTrackUrlsFromSearchHtml(html, title, artist);
                foreach (var candidate in found)
                {
                    if (combinedScores.TryGetValue(candidate.Url, out var existing))
                    {
                        if (candidate.Score > existing.Score)
                        {
                            combinedScores[candidate.Url] = (candidate.Score, existing.FirstIndex);
                        }
                    }
                    else
                    {
                        combinedScores[candidate.Url] = (candidate.Score, nextIndex++);
                    }
                }
            }

            if (combinedScores.Count == 0)
            {
                return null;
            }

            var candidates = new List<(string Url, int Score, int FirstIndex)>(combinedScores.Count);
            foreach (var item in combinedScores)
            {
                candidates.Add((item.Key, item.Value.Score, item.Value.FirstIndex));
            }
            candidates.Sort(static (a, b) =>
            {
                int cmp = b.Score.CompareTo(a.Score);
                return cmp != 0 ? cmp : a.FirstIndex.CompareTo(b.FirstIndex);
            });

            if (candidates.Count > 3)
            {
                candidates = candidates.GetRange(0, 3);
            }

            var probeTasks = new List<Task<SoundCloudCandidateProbe>>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                probeTasks.Add(ProbeSoundCloudCandidateAsync(
                    candidate.Url,
                    candidate.Score,
                    i,
                    title,
                    artist,
                    requireStrongMatch,
                    ct));
            }

            var probes = await Task.WhenAll(probeTasks);
            Array.Sort(probes, static (a, b) => a.Index.CompareTo(b.Index));

            for (int i = 0; i < probes.Length; i++)
            {
                var probe = probes[i];
                if (probe.IsMatch && !string.IsNullOrWhiteSpace(probe.ThumbnailUrl))
                {
                    return probe.ThumbnailUrl;
                }
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
}
