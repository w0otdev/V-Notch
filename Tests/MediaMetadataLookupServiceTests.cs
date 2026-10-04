using System.Net;
using System.Net.Http;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaMetadataLookupServiceTests
{
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
            if (request.RequestUri.Query.Contains("part=contentDetails"))
                return Json(new { items = new[] { new { contentDetails = new { duration = "PT90S" } } } });
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
        Assert.Equal(TimeSpan.FromSeconds(90), result.Duration);
        Assert.Equal(YouTubeLookupSource.OEmbed, result.Source);
        Assert.Equal(3, handler.Requests.Count);
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
    public async Task SoundCloudUsesOEmbedArtworkAndNormalizesItsResolution(string mode)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/oembed"
            ? Json(new { title = "Test song", author_name = "Artist", thumbnail_url = "https://i1.sndcdn.com/artworks-test-large.jpg" })
            : Body("<a href=\"/artist/test-song\">Test song</a><a href=\"/artist/test-song\">duplicate</a>"));
        using var client = new HttpClient(handler);
        var service = Service(client);
        string? result = mode == "direct"
            ? await service.TryGetSoundCloudArtworkFromUrlAsync("https://soundcloud.com/artist/test-song")
            : await service.TryGetSoundCloudArtworkUrlAsync("Test song", "Artist", requireStrongMatch: true);
        Assert.Equal("https://i1.sndcdn.com/artworks-test-t500x500.jpg", result);
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
        cancelled.Cancel();
        Assert.Null(await service.TrySearchYouTubeByTitleAsync("Test song", ct: cancelled.Token));
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
