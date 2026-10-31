using System.Net;
using System.Net.Http;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaMetadataLookupServiceTests
{
    [Fact]
    public async Task SearchRejectsUnrelatedFirstResultsFromEveryProvider()
    {
        string id = NewId();
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "www.googleapis.com")
            {
                Assert.EndsWith("/search", uri.AbsolutePath);
                return Json(new { items = new[] { new { id = new { videoId = id }, snippet = new { title = "Unrelated viral podcast", channelTitle = "Host" } } } });
            }
            if (uri.Host == "www.youtube.com") return Body("<html>no matching results</html>");
            if (uri.AbsolutePath == "/search")
                return Json(new { items = new[] { new { url = "/watch?v=" + id, title = "Unrelated viral podcast", uploaderName = "Host" } } });
            return Json(new[] { new { videoId = id, title = "Unrelated viral podcast", author = "Host" } });
        });
        using var client = new HttpClient(handler);
        var result = await Service(client, apiKey: "test-key-for-fixtures").TrySearchYouTubeByTitleAsync("Obscure underground track", "Artist");
        Assert.Null(result);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath is "/oembed" or "/youtube/v3/videos");
    }

    [Theory]
    [InlineData("", "Song")]
    [InlineData("!!!", "Song")]
    [InlineData("Song", "!!!")]
    [InlineData(" ", "Song")]
    public void EmptyNormalizedTitlesCannotMatchUnrelatedVideos(string title, string expected)
        => Assert.False(new YouTubeLookupResult { Title = title }.TitleMatches(expected));

    [Theory]
    [InlineData("api")]
    [InlineData("scrape")]
    public async Task EnrichmentCannotReplaceAMatchingSearchTitleWithUnrelatedMetadata(string source)
    {
        string id = NewId();
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "www.googleapis.com")
                return uri.AbsolutePath.EndsWith("/search")
                    ? Json(new { items = new[] { new { id = new { videoId = id }, snippet = new { title = "Requested song" } } } })
                    : Json(new { items = new[] { new { snippet = new { title = "Unrelated video", channelTitle = "Other artist" }, contentDetails = new { duration = "PT2M" } } } });
            if (uri.AbsolutePath == "/oembed") return Json(new { title = "Unrelated video", author_name = "Other artist" });
            if (uri.Host == "www.youtube.com") return Body(source == "scrape"
                ? "<script>var ytInitialData = {\"videoId\":\"" + id + "\",\"title\":{\"runs\":[{\"text\":\"Requested song\"}]}};</script>"
                : "<html>no results</html>");
            return uri.AbsolutePath == "/search" ? Json(new { items = Array.Empty<object>() }) : Json(Array.Empty<object>());
        });
        using var client = new HttpClient(handler);
        Assert.Null(await Service(client, apiKey: source == "api" ? "test-key-for-fixtures" : null)
            .TrySearchYouTubeByTitleAsync("Requested song"));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("scrape")]
    [InlineData("piped")]
    [InlineData("invidious")]
    public async Task SearchRejectsUnrelatedResultsWithoutEnrichingOrCachingThem(string source)
    {
        string id = NewId();
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/oembed") return Json(new { title = "Actual video title", author_name = "Actual artist" });
            if (uri.Host == "www.googleapis.com")
                return uri.AbsolutePath.EndsWith("/search")
                    ? Json(new { items = new[] { new { id = new { videoId = id }, snippet = new { title = "Unrelated result", channelTitle = "Other artist" } } } })
                    : Json(new { items = Array.Empty<object>() });
            if (uri.Host == "www.youtube.com")
                return Body(source == "scrape"
                    ? "<script>var ytInitialData = {\"videoId\":\"" + id + "\",\"title\":{\"runs\":[{\"text\":\"Unrelated result\"}]}};</script>"
                    : "<html>no results</html>");
            if (uri.AbsolutePath == "/search")
                return source == "piped"
                    ? Json(new { items = new[] { new { url = "/watch?v=" + id, title = "Unrelated result" } } })
                    : Json(new { items = Array.Empty<object>() });
            return source == "invidious"
                ? Json(new[] { new { videoId = id, title = "Unrelated result" } })
                : Json(Array.Empty<object>());
        });
        using var client = new HttpClient(handler);
        var service = Service(client, apiKey: source == "api" ? "test-key-for-fixtures" : null);
        Assert.Null(await service.TrySearchYouTubeByTitleAsync("Requested song", "Requested artist"));
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath is "/oembed" or "/youtube/v3/videos");
        var actual = await service.TryGetYouTubeVideoIdWithInfoAsync(id);
        Assert.Equal("Actual video title", actual?.Title);
        Assert.Contains(handler.Requests, uri => uri.AbsolutePath == "/oembed");
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v={0}")]
    [InlineData("https://youtu.be/{0}")]
    [InlineData("https://music.youtube.com/watch?v={0}")]
    [InlineData("https://www.youtube.com/embed/{0}")]
    [InlineData("https://www.youtube.com/v/{0}")]
    public async Task VideoUrlsResolveMetadataAndReuseCachedResults(string format)
    {
        string id = NewId();
        using var handler = new Handler(_ => Json(new { title = "Test song", author_name = "Test artist" }));
        using var client = new HttpClient(handler);
        var service = Service(client);
        string url = string.Format(format, id);
        var result = await service.TryGetYouTubeVideoInfoFromUrlAsync(url);
        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("Test song", result.Title);
        Assert.Equal("Test artist", result.Author);
        Assert.Equal(YouTubeLookupSource.OEmbed, result.Source);
        Assert.Same(result, await service.TryGetYouTubeVideoInfoFromUrlAsync(url));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.invalid/watch?v=abcdefghijk")]
    [InlineData("https://youtu.be/short")]
    public async Task InvalidUrlsAndOfflineSearchDoNotSendRequests(string url)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected request"));
        using var client = new HttpClient(handler);
        var service = Service(client, allowed: false);
        Assert.Null(await service.TryGetYouTubeVideoInfoFromUrlAsync(url));
        Assert.Null(await service.TryGetYouTubeVideoIdWithInfoAsync("Test song"));
        Assert.Null(await service.TrySearchYouTubeByTitleAsync("Test song"));
        Assert.Null(await service.TryGetSoundCloudArtworkUrlAsync("Test song"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DataApiMetadataIncludesDurationAndTheBestThumbnailAndSendsTheKeyOnlyInTheHeader()
    {
        string id = NewId();
        using var handler = new Handler(request =>
        {
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            Assert.Equal("test-key-for-fixtures", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.DoesNotContain("test-key", request.RequestUri.Query);
            return Json(new
            {
                items = new[] { new { snippet = new { title = "Test song", channelTitle = "Test artist",
                    thumbnails = new { @default = new { url = "https://example.invalid/small.jpg" },
                        maxres = new { url = "https://example.invalid/large.jpg" } } },
                    contentDetails = new { duration = "PT3M42S" } } }
            });
        });
        using var client = new HttpClient(handler);
        var result = await Service(client, apiKey: "test-key-for-fixtures").TryGetYouTubeVideoIdWithInfoAsync(id);
        Assert.NotNull(result);
        Assert.Equal(YouTubeLookupSource.DataApi, result.Source);
        Assert.Equal(TimeSpan.FromSeconds(222), result.Duration);
        Assert.Equal("https://example.invalid/large.jpg", result.ThumbnailUrl);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("error")]
    public async Task FailedDataApiFallsBackToOEmbedAndCanEnrichItsDuration(string failure)
    {
        string id = NewId();
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "www.youtube.com")
                return Json(new { title = "Fallback song", author_name = "Fallback artist" });
            return failure switch
            {
                "empty" => Json(new { items = Array.Empty<object>() }),
                "malformed" => Body("invalid json"),
                _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("temporary failure") }
            };
        });
        using var client = new HttpClient(handler);
        var result = await Service(client, apiKey: "test-key-for-fixtures").TryGetYouTubeVideoIdWithInfoAsync(id);
        Assert.NotNull(result);
        Assert.Equal("Fallback song", result.Title);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal(YouTubeLookupSource.OEmbed, result.Source);
        Assert.Single(handler.Requests, uri => uri.Host == "www.googleapis.com" && uri.AbsolutePath == "/youtube/v3/videos");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ScrapeParsesVideoRendererWithLongThumbnailListAndJsonEscapes()
    {
        string id = NewId();
        string longThumbnails = new string('x', 1100);
        string ytJson =
            "{\"contents\":{\"videoRenderer\":{\"videoId\":\"" + id + "\"," +
            "\"thumbnail\":{\"thumbnails\":[{\"url\":\"https://i.ytimg.com/vi/" + id + "/" + longThumbnails + ".jpg\",\"width\":1280,\"height\":720}]}," +
            "\"title\":{\"runs\":[{\"text\":\"Rock \\u0026 Roll \\\"Live\\\" &amp; Loud\"}]}," +
            "\"longBylineText\":{\"runs\":[{\"text\":\"Band \\u0026 Crew\"}]}}}}";

        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/oembed")
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            if (uri.Host == "www.youtube.com")
                return Body("<script>var ytInitialData = " + ytJson + ";</script>");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var service = Service(client);
        var result = await service.TrySearchYouTubeByTitleAsync("Rock & Roll \"Live\" & Loud", "Band & Crew");
        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("Rock & Roll \"Live\" & Loud", result.Title);
        Assert.Equal("Band & Crew", result.Author);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("scrape")]
    [InlineData("alternate-scrape")]
    [InlineData("piped")]
    [InlineData("invidious")]
    public async Task SearchResolvesMetadataFromEachFallback(string source)
    {
        string id = NewId();
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "www.googleapis.com")
            {
                if (uri.AbsolutePath.EndsWith("/search"))
                    return Json(new { items = new[] { new { id = new { videoId = id }, snippet = new { title = "Test song", channelTitle = "Artist" } } } });
                return Json(new { items = new[] { new { snippet = new { title = "Test song", channelTitle = "Artist" }, contentDetails = new { duration = "PT2M" } } } });
            }
            if (uri.AbsolutePath == "/oembed") return Json(new { title = "Test song", author_name = "Artist" });
            if (uri.Host == "www.youtube.com")
            {
                string prefix = source == "alternate-scrape" ? "window[\"ytInitialData\"] = " : "var ytInitialData = ";
                string data = "{\"videoId\":\"" + id + "\",\"title\":{\"runs\":[{\"text\":\"Test song\"}]},\"ownerText\":{\"runs\":[{\"text\":\"Artist\"}]}}";
                return Body(source.Contains("scrape") ? "<script>" + prefix + data + ";</script>" : "<html>no results</html>");
            }
            if (uri.AbsolutePath == "/search")
                return source == "piped" ? Json(new { items = new[] { new { url = "/watch?v=" + id, title = "Test song", uploaderName = "Artist", duration = 120, thumbnail = "https://example.invalid/art.jpg" } } }) : Json(new { items = Array.Empty<object>() });
            return Json(new[] { new { videoId = id, title = "Test song", author = "Artist", lengthSeconds = 120 } });
        });
        using var client = new HttpClient(handler);
        var service = Service(client, apiKey: source == "api" ? "test-key-for-fixtures" : null);
        var result = await service.TrySearchYouTubeByTitleAsync("Test song", "Artist");
        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("Test song", result.Title);
        Assert.Equal("Artist", result.Author);
        Assert.Equal(source == "api" ? YouTubeLookupSource.DataApi : YouTubeLookupSource.OEmbed, result.Source);
        if (source is "api" or "piped" or "invidious") Assert.Equal(TimeSpan.FromMinutes(2), result.Duration);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("search")]
    public async Task SoundCloudUsesTheArtworkUrlReturnedByOEmbed(string mode)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/oembed"
            ? Json(new { title = "Test song", author_name = "Artist", thumbnail_url = "https://i1.sndcdn.com/artworks-test-large.jpg" })
            : Body("<a href=\"/artist/test-song\">Test song</a><a href=\"/artist/test-song\">duplicate</a>"));
        using var client = new HttpClient(handler);
        var service = Service(client);
        string? result = mode == "direct"
            ? await service.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/artist/test-song")
            : await service.TryGetSoundCloudArtworkUrlAsync("Test song", "Artist", requireStrongMatch: true);
        Assert.Equal("https://i1.sndcdn.com/artworks-test-large.jpg", result);
        Assert.Equal(mode == "direct" ? 1 : 3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid json")]
    [InlineData("{}")]
    public async Task InvalidAndMissingOEmbedDataReturnsNoArtwork(string body)
    {
        using var handler = new Handler(_ => Body(body));
        using var client = new HttpClient(handler);
        var service = Service(client);
        Assert.Null(await service.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/artist/test-song"));
        Assert.Null(await service.TryGetSoundCloudArtworkUrlAsync("Test song", "Artist"));
    }

    [Fact]
    public async Task RequestFailuresAndCancellationReturnNoMetadata()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("fixture network failure"));
        using var client = new HttpClient(handler);
        var service = Service(client);
        Assert.Null(await service.TryGetYouTubeVideoIdWithInfoAsync(NewId()));
        Assert.Null(await service.TrySearchYouTubeByTitleAsync("Test song"));
        Assert.Null(await service.TryGetSoundCloudArtworkUrlAsync("Test song"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Null(await service.TryGetYouTubeVideoIdWithInfoAsync(NewId(), ct: cancelled.Token));
        Assert.Null(await service.TryGetYouTubeVideoInfoFromUrlAsync($"https://www.youtube.com/watch?v={NewId()}", cancelled.Token));
        Assert.Null(await service.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/artist/test-song", cancelled.Token));
        Assert.Null(await service.TrySearchYouTubeByTitleAsync("Test song", ct: cancelled.Token));

        using var cancelMidFlightHandler = new Handler(_ => throw new OperationCanceledException());
        using var cancelMidFlightClient = new HttpClient(cancelMidFlightHandler);
        var cancelMidFlightService = Service(cancelMidFlightClient);
        Assert.Null(await cancelMidFlightService.TryGetYouTubeVideoIdWithInfoAsync(NewId()));
        Assert.Null(await cancelMidFlightService.TryGetYouTubeVideoInfoFromUrlAsync($"https://www.youtube.com/watch?v={NewId()}"));
        Assert.Null(await cancelMidFlightService.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/artist/test-song"));
    }

    [Fact]
    public async Task SoundCloudUrlLookupRespectsOfflineGateAndRejectsMismatchedTrackOrNavigationUrl()
    {
        using var offlineHandler = new Handler(_ => throw new InvalidOperationException("Unexpected request"));
        using var offlineClient = new HttpClient(offlineHandler);
        var offlineService = Service(offlineClient, allowed: false);
        Assert.Null(await offlineService.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/artist/song"));
        Assert.Empty(offlineHandler.Requests);

        using var handler = new Handler(_ => Json(new
        {
            title = "Different Song by Different Artist",
            author_name = "Different Artist",
            thumbnail_url = "https://i1.sndcdn.com/artworks-valid-t500x500.jpg"
        }));
        using var client = new HttpClient(handler);
        var service = Service(client, allowed: true);

        Assert.Null(await service.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/you/likes"));
        Assert.Empty(handler.Requests);

        Assert.Null(await service.TryGetSoundCloudArtworkFromUrlAsync(
            "https://soundcloud.com/artist/song",
            expectedTitle: "Expected Track",
            expectedArtist: "Expected Artist"));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("NO POLE ULTIMATE 2", "NV So Lit")]
    [InlineData("NO POLE ULTIMATE", "Other uploader")]
    [InlineData(null, null)]
    public async Task SoundCloudBrowserUrlMustIdentifyThePlayingTrack(string? title, string? author)
    {
        using var handler = new Handler(_ => Json(new
        {
            title,
            author_name = author,
            thumbnail_url = "https://i1.sndcdn.com/artworks-wrong-large.jpg"
        }));
        using var client = new HttpClient(handler);
        Assert.Null(await Service(client).TryGetSoundCloudArtworkFromUrlAsync(
            "https://soundcloud.com/nv-so-lit/no-pole-ultimate-2", "NO POLE ULTIMATE", "NV So Lit"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoundCloudSearchChoosesTheOriginalInsteadOfASimilarlyNamedVersion(bool strict)
    {
        const string html = "<a href='/nv-so-lit/no-pole-ultimate-2'>Sequel</a>" +
            "<a href='/nv-so-lit/no-pole-ultimate-remix'>Remix</a>" +
            "<a href='/nv-so-lit/no-pole-ultimate-sped-up'>Sped up</a>" +
            "<a href='/nv-so-lit/no-pole-ultimate'>Original</a>";
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath != "/oembed") return Body(html);
            string trackUrl = Uri.UnescapeDataString(uri.Query);
            bool original = trackUrl.EndsWith("/no-pole-ultimate", StringComparison.Ordinal);
            return Json(new
            {
                title = original ? "NO POLE ULTIMATE by NV So Lit" : "NO POLE ULTIMATE 2 by NV So Lit",
                author_name = "NV So Lit",
                thumbnail_url = original ? "https://i1.sndcdn.com/artworks-original-large.jpg" : "https://i1.sndcdn.com/artworks-wrong-large.jpg"
            });
        });
        using var client = new HttpClient(handler);
        Assert.Equal("https://i1.sndcdn.com/artworks-original-large.jpg",
            await Service(client).TryGetSoundCloudArtworkUrlAsync("NO POLE ULTIMATE", "NV So Lit", strict));
    }

    private static MediaMetadataLookupService Service(HttpClient client, string? apiKey = null, bool allowed = true) =>
        new(client, client, () => allowed, () => apiKey);
    private static string NewId() => Guid.NewGuid().ToString("N")[..11];
    private static HttpResponseMessage Json(object value) => Body(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Body(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(response(request));
        }
    }
}
