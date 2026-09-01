using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VNotch.Models;

namespace VNotch.Services;

public sealed class MediaMetadataLookupService : IMediaMetadataLookupService
{
    private static readonly HttpClient _httpClient = new();

    static MediaMetadataLookupService()
    {
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<YouTubeLookupResult?> TryGetYouTubeVideoIdWithInfoAsync(string title, string artist = "", CancellationToken ct = default)
    {
        if (!IsOnlineArtworkAllowed())
            return null;

        try
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string cleanTitle = title.Trim();

            if (cleanTitle.Length == 11 && Regex.IsMatch(cleanTitle, "^[a-zA-Z0-9_-]{11}$"))
            {
                var validated = await ResolveVideoIdAsync(cleanTitle, ct);
                if (validated != null)
                    return validated;
            }

            var urlMatch = Regex.Match(cleanTitle, @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=)([a-zA-Z0-9_-]{11})");
            if (urlMatch.Success)
            {
                string extractedId = urlMatch.Groups[1].Value;
                var validated = await ResolveVideoIdAsync(extractedId, ct);
                if (validated != null)
                    return validated;
            }

        }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-SEARCH", ex.ToString());
        }

        return null;
    }

    public async Task<YouTubeLookupResult?> TrySearchYouTubeByTitleAsync(string title, string artist = "", CancellationToken ct = default)
    {
        if (!IsOnlineArtworkAllowed() || string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            string query = string.IsNullOrWhiteSpace(artist) || MediaPlatformExtensions.ParsePlatform(artist) == MediaPlatform.YouTube
                ? title
                : $"{title} {artist}";

            string? apiKey = GetYouTubeApiKey();
            if (!string.IsNullOrEmpty(apiKey) && !IsQuotaCooldownActive())
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-TITLE-SEARCH", ex.ToString());
        }

        return null;
    }

    private async Task<YouTubeLookupResult?> TrySearchViaDataApiAsync(string query, string originalTitle, string apiKey, CancellationToken ct)
    {
        try
        {
            string url = $"https://www.googleapis.com/youtube/v3/search?part=snippet&q={Uri.EscapeDataString(query)}&type=video&maxResults=3&fields=items(id/videoId,snippet(title,channelTitle))&key={apiKey}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await _httpClient.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (LooksLikeQuotaExceeded(body))
                    TripQuotaCooldown();
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            foreach (var item in items.EnumerateArray())
            {
                string? videoId = null;
                string? resultTitle = null;
                string? channelTitle = null;

                if (item.TryGetProperty("id", out var idEl) && idEl.TryGetProperty("videoId", out var vidEl))
                    videoId = vidEl.GetString();
                if (item.TryGetProperty("snippet", out var snippet))
                {
                    if (snippet.TryGetProperty("title", out var titleEl))
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
                    var enriched = await ResolveVideoIdAsync(videoId!, ct);
                    if (enriched != null)
                    {
                        RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                            $"data-api-search-ok query='{query}' videoId={videoId} title='{resultTitle}'");
                        return enriched;
                    }
                }
            }

            var firstItem = items[0];
            string? firstVideoId = null;
            if (firstItem.TryGetProperty("id", out var firstIdEl) && firstIdEl.TryGetProperty("videoId", out var firstVidEl))
                firstVideoId = firstVidEl.GetString();

            if (!string.IsNullOrEmpty(firstVideoId))
            {
                var enrichedFirst = await ResolveVideoIdAsync(firstVideoId!, ct);
                if (enrichedFirst != null)
                {
                    RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                        $"data-api-search-first-result query='{query}' videoId={firstVideoId} title='{enrichedFirst.Title}'");
                    return enrichedFirst;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-TITLE-SEARCH-API", ex.ToString());
        }

        return null;
    }

    private async Task<YouTubeLookupResult?> TrySearchViaPipedAsync(string query, string originalTitle, CancellationToken ct)
    {
        string[] pipedInstances = { "pipedapi.kavin.rocks", "pipedapi.adminforge.de", "pipedapi.in.projectsegfault.com" };

        foreach (var instance in pipedInstances)
        {
            try
            {
                string url = $"https://{instance}/search?q={Uri.EscapeDataString(query)}&filter=videos";

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                var response = await _httpClient.GetAsync(url, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
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
                    if (item.TryGetProperty("title", out var titleEl))
                        itemTitle = titleEl.GetString();
                    if (item.TryGetProperty("uploaderName", out var uploaderEl))
                        uploaderName = uploaderEl.GetString();
                    if (item.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                        durationSec = durEl.GetInt64();
                    if (item.TryGetProperty("thumbnail", out var thumbEl))
                        thumbnailUrl = thumbEl.GetString();

                    if (string.IsNullOrEmpty(itemUrl))
                        continue;

                    var match = Regex.Match(itemUrl!, @"v=([a-zA-Z0-9_-]{11})");
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
                        RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                            $"piped-search-ok instance={instance} query='{query}' videoId={videoId} title='{itemTitle}'");
                        CacheVideo(videoId, candidate);
                        return candidate;
                    }
                }

                if (items.GetArrayLength() > 0)
                {
                    var first = items[0];
                    string? firstUrl = first.TryGetProperty("url", out var fUrlEl) ? fUrlEl.GetString() : null;
                    if (!string.IsNullOrEmpty(firstUrl))
                    {
                        var firstMatch = Regex.Match(firstUrl!, @"v=([a-zA-Z0-9_-]{11})");
                        if (firstMatch.Success)
                        {
                            string firstVideoId = firstMatch.Groups[1].Value;
                            string? firstTitle = first.TryGetProperty("title", out var fTitleEl) ? fTitleEl.GetString() : null;
                            string? firstUploader = first.TryGetProperty("uploaderName", out var fUpEl) ? fUpEl.GetString() : null;
                            long firstDur = first.TryGetProperty("duration", out var fDurEl) && fDurEl.ValueKind == JsonValueKind.Number ? fDurEl.GetInt64() : 0;
                            string? firstThumb = first.TryGetProperty("thumbnail", out var fThumbEl) ? fThumbEl.GetString() : null;

                            var result = new YouTubeLookupResult
                            {
                                Id = firstVideoId,
                                Title = firstTitle,
                                Author = firstUploader,
                                Duration = TimeSpan.FromSeconds(firstDur),
                                ThumbnailUrl = firstThumb,
                                Source = YouTubeLookupSource.OEmbed,
                            };

                            RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                                $"piped-search-first-result instance={instance} query='{query}' videoId={firstVideoId} title='{firstTitle}'");
                            CacheVideo(firstVideoId, result);
                            return result;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
        }

        string[] invidiousInstances = { "vid.puffyan.us", "invidious.fdn.fr", "invidious.nerdvpn.de" };

        foreach (var instance in invidiousInstances)
        {
            try
            {
                string url = $"https://{instance}/api/v1/search?q={Uri.EscapeDataString(query)}&type=video";

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                var response = await _httpClient.GetAsync(url, timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    continue;

                foreach (var item in root.EnumerateArray())
                {
                    string? videoId = item.TryGetProperty("videoId", out var vidEl) ? vidEl.GetString() : null;
                    string? itemTitle = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
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
                        RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                            $"invidious-search-ok instance={instance} query='{query}' videoId={videoId} title='{itemTitle}'");
                        CacheVideo(videoId!, candidate);
                        return candidate;
                    }
                }

                if (root.GetArrayLength() > 0)
                {
                    var first = root[0];
                    string? firstVideoId = first.TryGetProperty("videoId", out var fVidEl) ? fVidEl.GetString() : null;
                    if (!string.IsNullOrEmpty(firstVideoId))
                    {
                        string? firstTitle = first.TryGetProperty("title", out var fTitleEl) ? fTitleEl.GetString() : null;
                        string? firstAuthor = first.TryGetProperty("author", out var fAuthEl) ? fAuthEl.GetString() : null;
                        long firstLen = first.TryGetProperty("lengthSeconds", out var fLenEl) && fLenEl.ValueKind == JsonValueKind.Number ? fLenEl.GetInt64() : 0;

                        var result = new YouTubeLookupResult
                        {
                            Id = firstVideoId,
                            Title = firstTitle,
                            Author = firstAuthor,
                            Duration = TimeSpan.FromSeconds(firstLen),
                            ThumbnailUrl = null,
                            Source = YouTubeLookupSource.OEmbed,
                        };

                        RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                            $"invidious-search-first-result instance={instance} query='{query}' videoId={firstVideoId} title='{firstTitle}'");
                        CacheVideo(firstVideoId!, result);
                        return result;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
        }

        return null;
    }

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

            var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            string html = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            const string marker = "var ytInitialData = ";
            int startIdx = html.IndexOf(marker, StringComparison.Ordinal);
            if (startIdx < 0)
            {
                const string altMarker = "window[\"ytInitialData\"] = ";
                startIdx = html.IndexOf(altMarker, StringComparison.Ordinal);
                if (startIdx < 0)
                    return null;
                startIdx += altMarker.Length;
            }
            else
            {
                startIdx += marker.Length;
            }

            int endIdx = html.IndexOf(";</script>", startIdx, StringComparison.Ordinal);
            if (endIdx < 0)
                endIdx = html.IndexOf("};\n", startIdx, StringComparison.Ordinal);
            if (endIdx < 0)
                return null;

            string jsonStr = html[startIdx..endIdx];
            jsonStr = jsonStr.TrimEnd(';');

            var videoIds = new List<(string id, string? title, string? channel)>();
            var videoIdPattern = new Regex(@"""videoId""\s*:\s*""([a-zA-Z0-9_-]{11})""");

            var idMatches = videoIdPattern.Matches(jsonStr);
            var seenIds = new HashSet<string>();

            foreach (Match idMatch in idMatches)
            {
                string videoId = idMatch.Groups[1].Value;
                if (!seenIds.Add(videoId))
                    continue;

                string? videoTitle = null;
                string? channelName = null;
                int searchStart = idMatch.Index;
                int searchLen = Math.Min(800, jsonStr.Length - searchStart);
                string context = jsonStr.Substring(searchStart, searchLen);

                var titleMatch = Regex.Match(context, @"""title"":\{""runs"":\[\{""text"":""([^""]+)""");
                if (titleMatch.Success)
                    videoTitle = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value);

                var channelMatch = Regex.Match(context, @"""(?:ownerText|longBylineText|shortBylineText)"":\{""runs"":\[\{""text"":""([^""]+)""");
                if (channelMatch.Success)
                    channelName = System.Net.WebUtility.HtmlDecode(channelMatch.Groups[1].Value);

                videoIds.Add((videoId, videoTitle, channelName));

                if (videoIds.Count >= 5)
                    break;
            }

            if (videoIds.Count == 0)
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
                        RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                            $"yt-scrape-ok query='{query}' videoId={videoId} title='{videoTitle}' duration={enriched.Duration}");
                        CacheVideo(videoId, enriched);
                        return enriched;
                    }
                    RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                        $"yt-scrape-ok query='{query}' videoId={videoId} title='{videoTitle}' (no enrich)");
                    CacheVideo(videoId, candidate);
                    return candidate;
                }
            }

            var (firstId, firstTitle2, firstChannel) = videoIds[0];
            var enrichedFirst = await ResolveVideoIdAsync(firstId, ct);
            if (enrichedFirst != null)
            {
                RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                    $"yt-scrape-first-result query='{query}' videoId={firstId} title='{firstTitle2}' duration={enrichedFirst.Duration}");
                CacheVideo(firstId, enrichedFirst);
                return enrichedFirst;
            }

            var firstResult = new YouTubeLookupResult
            {
                Id = firstId,
                Title = firstTitle2,
                Author = firstChannel,
                Duration = TimeSpan.Zero,
                ThumbnailUrl = null,
                Source = YouTubeLookupSource.OEmbed,
            };

            RuntimeLog.Log("META-YOUTUBE-TITLE-SEARCH",
                $"yt-scrape-first-result query='{query}' videoId={firstId} title='{firstTitle2}' (no enrich)");
            CacheVideo(firstId, firstResult);
            return firstResult;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-TITLE-SEARCH-SCRAPE", ex.ToString());
        }

        return null;
    }

    public async Task<YouTubeLookupResult?> TryGetYouTubeVideoInfoFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var match = Regex.Match(url, @"(?:youtube\.com/watch\?.*v=|youtu\.be/|music\.youtube\.com/watch\?.*v=|youtube\.com/embed/|youtube\.com/v/)([a-zA-Z0-9_-]{11})");
            if (!match.Success)
                return null;

            string videoId = match.Groups[1].Value;
            return await ResolveVideoIdAsync(videoId, ct);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("META-YOUTUBE-URL", ex.ToString());
        }

        return null;
    }

    private async Task<YouTubeLookupResult?> ResolveVideoIdAsync(string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        if (TryGetCachedVideo(videoId, out var cached) && cached != null)
        {
            RuntimeLog.Log("META-YOUTUBE-API",
                $"cache-hit videoId={videoId} source={cached.Source} duration={cached.Duration} thumb={(string.IsNullOrEmpty(cached.ThumbnailUrl) ? "(none)" : "data-api")}");
            return cached;
        }

        string? apiKey = GetYouTubeApiKey();
        if (!string.IsNullOrEmpty(apiKey))
        {
            var apiResult = await TryGetVideoFromDataApiAsync(videoId, apiKey, ct);
            if (apiResult != null)
            {
                CacheVideo(videoId, apiResult);
                return apiResult;
            }
            RuntimeLog.Log("META-YOUTUBE-API", $"data-api-miss videoId={videoId} -> falling back to oEmbed");
        }
        else
        {
            RuntimeLog.Log("META-YOUTUBE-API", $"data-api-disabled videoId={videoId} -> using oEmbed only");
        }

        var oembed = await ValidateVideoIdWithOEmbedAsync(videoId, ct);
        if (oembed == null)
            return null;

        if (!string.IsNullOrEmpty(apiKey))
        {
            oembed = await EnrichWithDurationAsync(oembed, apiKey, ct) ?? oembed;
        }

        CacheVideo(videoId, oembed);
        return oembed;
    }

    public async Task<string?> TryGetSoundCloudArtworkFromUrlAsync(string soundCloudUrl, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(soundCloudUrl))
                return null;

            if (!soundCloudUrl.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase))
                return null;

            var result = await TryGetSoundCloudOEmbedAsync(soundCloudUrl, ct);
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                string normalized = NormalizeSoundCloudArtworkUrl(result.ThumbnailUrl!);
                if (!IsLikelySoundCloudPlaceholderArtworkUrl(normalized))
                    return normalized;
            }
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

            string? oembedTitle = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
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

    private async Task<YouTubeLookupResult?> TryGetVideoFromDataApiAsync(string videoId, string apiKey, CancellationToken ct)
    {
        if (IsQuotaCooldownActive())
            return null;

        try
        {
            string url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails&id={Uri.EscapeDataString(videoId)}&fields=items(snippet(title,channelTitle,thumbnails),contentDetails/duration)&key={apiKey}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2500);

            using var response = await _httpClient.GetAsync(url, timeoutCts.Token);
            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (LooksLikeQuotaExceeded(json))
                {
                    TripQuotaCooldown();
                    RuntimeLog.Log("META-YOUTUBE-API", "quotaExceeded — Data API disabled until tomorrow, falling back to oEmbed.");
                }
                else
                {
                    RuntimeLog.Log("META-YOUTUBE-API", $"HTTP {(int)response.StatusCode} for videoId={videoId}");
                }
                return null;
            }

            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return null;

            var item = items[0];
            string? title = null;
            string? channel = null;
            string? thumbnailUrl = null;
            TimeSpan duration = TimeSpan.Zero;

            if (item.TryGetProperty("snippet", out var snippet))
            {
                if (snippet.TryGetProperty("title", out var titleEl)) title = titleEl.GetString();
                if (snippet.TryGetProperty("channelTitle", out var channelEl)) channel = channelEl.GetString();
                if (snippet.TryGetProperty("thumbnails", out var thumbs))
                    thumbnailUrl = PickBestThumbnail(thumbs);
            }

            if (item.TryGetProperty("contentDetails", out var details) &&
                details.TryGetProperty("duration", out var durationEl))
            {
                string? iso = durationEl.GetString();
                if (!string.IsNullOrEmpty(iso))
                    duration = ParseIso8601Duration(iso);
            }

            RuntimeLog.Log("META-YOUTUBE-API",
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
            RuntimeLog.Error("META-YOUTUBE-API", ex.ToString());
            return null;
        }
    }
    private static string? PickBestThumbnail(JsonElement thumbnails)
        => YouTubeMetadataParsing.PickBestThumbnail(thumbnails);

    private static bool LooksLikeQuotaExceeded(string body)
        => YouTubeMetadataParsing.LooksLikeQuotaExceeded(body);

    private static long _quotaCooldownUntilTicks;

    private static bool IsQuotaCooldownActive()
        => Volatile.Read(ref _quotaCooldownUntilTicks) > DateTime.UtcNow.Ticks;

    private static void TripQuotaCooldown()
    {
        var resumeAt = DateTime.UtcNow.Date.AddDays(1).AddHours(8);
        Volatile.Write(ref _quotaCooldownUntilTicks, resumeAt.Ticks);
    }

    private static readonly object _videoCacheLock = new();
    private static readonly Dictionary<string, (YouTubeLookupResult Result, DateTime ExpiresUtc)> _videoCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan _videoCacheTtl = TimeSpan.FromHours(6);
    private const int VideoCacheMaxEntries = 256;

    private static bool TryGetCachedVideo(string videoId, out YouTubeLookupResult? result)
    {
        lock (_videoCacheLock)
        {
            if (_videoCache.TryGetValue(videoId, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
            {
                result = entry.Result;
                return true;
            }
        }
        result = null;
        return false;
    }

    private static void CacheVideo(string videoId, YouTubeLookupResult result)
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
            _videoCache[videoId] = (result, DateTime.UtcNow.Add(_videoCacheTtl));
        }
    }

    private async Task<YouTubeLookupResult?> EnrichWithDurationAsync(YouTubeLookupResult result, string apiKey, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(result.Id))
                return result;

            string url = $"https://www.googleapis.com/youtube/v3/videos?part=contentDetails&id={result.Id}&fields=items/contentDetails/duration&key={apiKey}";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(2000);

            string json = await _httpClient.GetStringAsync(url, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(json))
                return result;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                return result;

            var firstItem = items[0];
            if (firstItem.TryGetProperty("contentDetails", out var details) &&
                details.TryGetProperty("duration", out var durationEl))
            {
                string? isoDuration = durationEl.GetString();
                if (!string.IsNullOrEmpty(isoDuration))
                {
                    var duration = ParseIso8601Duration(isoDuration);
                    return new YouTubeLookupResult
                    {
                        Id = result.Id,
                        Title = result.Title,
                        Author = result.Author,
                        Duration = duration,
                        ThumbnailUrl = result.ThumbnailUrl,
                        Source = result.Source,
                    };
                }
            }

            return result;
        }
        catch
        {
            return result;
        }
    }

    private static TimeSpan ParseIso8601Duration(string iso)
        => YouTubeMetadataParsing.ParseIso8601Duration(iso);

    private static string? GetYouTubeApiKey()
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string settingsPath = Path.Combine(appDataPath, "V-Notch", "settings.json");

            if (!File.Exists(settingsPath))
                return null;

            string json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("EnableYouTubeApi", out var enabledEl) && enabledEl.GetBoolean())
            {
                if (root.TryGetProperty("YouTubeApiKey", out var keyEl))
                {
                    string? key = DataProtection.Unprotect(keyEl.GetString())?.Trim();
                    if (!string.IsNullOrEmpty(key) && key.Length > 10)
                        return key;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("YOUTUBE-API-KEY", $"Failed to read API key from settings: {ex.Message}");
        }

        return null;
    }

    public static bool IsOnlineArtworkAllowed()
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string settingsPath = Path.Combine(appDataPath, "V-Notch", "settings.json");
            if (!File.Exists(settingsPath)) return true;

            string json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty(nameof(NotchSettings.EnableLocalOnlyMode), out var localOnly) && localOnly.GetBoolean())
                return false;

            if (root.TryGetProperty(nameof(NotchSettings.EnableOnlineArtworkLookup), out var onlineArt) && !onlineArt.GetBoolean())
                return false;
        }
        catch
        {
        }

        return true;
    }

    public async Task<string?> TryGetSoundCloudArtworkUrlAsync(string title, string artist = "", bool requireStrongMatch = false, CancellationToken ct = default)
    {
        if (!IsOnlineArtworkAllowed())
            return null;

        try
        {
            var directTrackUrl = ExtractSoundCloudTrackUrl(title);
            if (!string.IsNullOrEmpty(directTrackUrl))
            {
                var direct = await TryGetSoundCloudOEmbedAsync(directTrackUrl, ct);
                if (!string.IsNullOrWhiteSpace(direct.ThumbnailUrl))
                {
                    string normalizedDirectThumbnail = NormalizeSoundCloudArtworkUrl(direct.ThumbnailUrl!);
                    if (!IsLikelySoundCloudPlaceholderArtworkUrl(normalizedDirectThumbnail))
                    {
                        return normalizedDirectThumbnail;
                    }
                }
            }

            string cleanedTitle = SanitizeSearchText(title);
            string cleanedArtist = SanitizeSearchText(artist);
            string query = cleanedTitle;
            if (!string.IsNullOrWhiteSpace(artist) &&
                MediaPlatformExtensions.ParsePlatform(artist) is not (MediaPlatform.SoundCloud or MediaPlatform.Browser))
            {
                query = $"{cleanedTitle} {cleanedArtist}".Trim();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var combinedScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
            foreach (var html in searchHtmls)
            {
                if (string.IsNullOrWhiteSpace(html))
                {
                    continue;
                }

                var found = ExtractSoundCloudTrackUrlsFromSearchHtml(html, title, artist);
                foreach (var candidate in found)
                {
                    if (combinedScores.TryGetValue(candidate.Url, out int existing))
                    {
                        if (candidate.Score > existing)
                        {
                            combinedScores[candidate.Url] = candidate.Score;
                        }
                    }
                    else
                    {
                        combinedScores[candidate.Url] = candidate.Score;
                    }
                }
            }

            if (combinedScores.Count == 0)
            {
                return null;
            }

            var candidates = new List<(string Url, int Score)>();
            foreach (var item in combinedScores)
            {
                candidates.Add((item.Key, item.Value));
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

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

            string normalizedThumb = NormalizeSoundCloudArtworkUrl(result.ThumbnailUrl!);
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
            string? oembedTitle = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
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
