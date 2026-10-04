using System.Net;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class YouTubeSubtitleHttpTests
{
    private const string Video = "fixture1234";
    private const string Xml = "<transcript><text start='2'>Second line</text><text start='1'>First line</text></transcript>";
    private const string Json3 = "{\"events\":[{\"tStartMs\":2000,\"segs\":[{\"utf8\":\"Second line\"}]},{\"tStartMs\":1000,\"segs\":[{\"utf8\":\"First line\"}]}]}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerAndCaptionResponsesProduceSortedLinesAndReuseCacheUntilForced(bool json3)
    {
        int requests = 0;
        using var client = Client((request, _) =>
        {
            requests++;
            if (request.Method == HttpMethod.Post) return Task.FromResult(Response(Player()));
            Assert.DoesNotContain("fmt=srv3", request.RequestUri!.Query);
            Assert.Contains("com.google.android.youtube", request.Headers.UserAgent.ToString());
            return Task.FromResult(Response(json3 ? Json3 : Xml));
        });
        using var service = Service(client);
        var first = await service.FetchSubtitlesAsync(Video);
        Assert.Equal(new[] { "First line", "Second line" }, first!.Select(line => line.Text));
        Assert.Equal(TimeSpan.FromSeconds(1), first[0].Time);
        Assert.Same(first, await service.FetchSubtitlesAsync(Video));
        Assert.Equal(2, requests);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video, force: true));
        Assert.Equal(4, requests);
        service.Reset();
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        Assert.Equal(6, requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task EmptyVideoIdentifiersDoNotIssueRequests(string input)
    {
        int calls = 0;
        using var client = Client((_, _) => { calls++; return Task.FromResult(Response("{}")); });
        using var service = Service(client);
        Assert.Null(await service.FetchSubtitlesAsync(input == "null" ? null! : input));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("exception")]
    public async Task PlayerFailuresRetryAndRecoverOnTheSecondAttempt(string failure)
    {
        int playerCalls = 0;
        using var client = Client((request, _) =>
        {
            if (request.Method != HttpMethod.Post) return Task.FromResult(Response(Xml));
            if (++playerCalls > 1) return Task.FromResult(Response(Player()));
            return failure switch
            {
                "status" => Task.FromResult(Response("rate limited", HttpStatusCode.TooManyRequests)),
                "missing" => Task.FromResult(Response("{\"playabilityStatus\":{\"status\":\"ERROR\",\"reason\":\"Fixture\"}}")),
                "malformed" => Task.FromResult(Response("invalid JSON")),
                _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("Fixture offline"))
            };
        });
        using var service = Service(client);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        Assert.Equal(2, playerCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingCaptionTracksRefreshThePlayerKeyAndRetryBeforeUsingFallback(bool keyFound)
    {
        int playerCalls = 0, watchCalls = 0, fallbacks = 0;
        var keys = new List<string>();
        using var client = Client((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                playerCalls++;
                keys.Add(request.RequestUri!.Query);
                return Task.FromResult(Response(playerCalls <= 2 ? "{}" : Player()));
            }
            if (request.RequestUri!.AbsolutePath == "/watch")
            {
                watchCalls++;
                Assert.Contains("en-US", request.Headers.AcceptLanguage.ToString());
                return Task.FromResult(Response(keyFound ? "\"INNERTUBE_API_KEY\": \"fixture-new-key\"" : "no key"));
            }
            return Task.FromResult(Response(Xml));
        });
        using var service = new YouTubeSubtitleService(client, (_, _) => { fallbacks++; return Task.FromResult<List<LyricLine>?>(null); });
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        Assert.Equal(3, playerCalls);
        Assert.Equal(1, watchCalls);
        if (keyFound) Assert.Contains("fixture-new-key", keys.Last());
        else Assert.Equal(keys[0], keys.Last());
        Assert.Equal(0, fallbacks);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("placeholder")]
    [InlineData("status")]
    [InlineData("malformed")]
    [InlineData("exception")]
    public async Task UnusableCaptionDownloadsUseTheFallbackAndCacheTheResult(string failure)
    {
        var fallback = new List<LyricLine> { new(TimeSpan.Zero, "Fallback caption") };
        int fallbacks = 0;
        using var client = Client((request, _) => request.Method == HttpMethod.Post ? Task.FromResult(Response(Player())) : failure switch
        {
            "empty" => Task.FromResult(Response("  ")),
            "placeholder" => Task.FromResult(Response("<transcript><text start='0'>[music]</text></transcript>")),
            "status" => Task.FromResult(Response("unavailable", HttpStatusCode.ServiceUnavailable)),
            "malformed" => Task.FromResult(Response("{invalid")),
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("Fixture offline"))
        });
        using var service = new YouTubeSubtitleService(client, (_, _) => { fallbacks++; return Task.FromResult<List<LyricLine>?>(fallback); });
        Assert.Same(fallback, await service.FetchSubtitlesAsync(Video));
        Assert.Same(fallback, await service.FetchSubtitlesAsync(Video));
        Assert.Equal(1, fallbacks);
    }

    [Fact]
    public async Task FailedFallbackInvalidatesTheFetchSoALaterRequestCanRecover()
    {
        int fallbacks = 0;
        using var client = Client((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Post ? Player() : "")));
        using var service = new YouTubeSubtitleService(client, (_, _) => ++fallbacks == 1
            ? Task.FromException<List<LyricLine>?>(new InvalidOperationException("Fixture failure"))
            : Task.FromResult<List<LyricLine>?>([new(TimeSpan.Zero, "Recovered")]));
        Assert.Null(await service.FetchSubtitlesAsync(Video));
        Assert.Equal("Recovered", Assert.Single((await service.FetchSubtitlesAsync(Video))!).Text);
        Assert.Equal(2, fallbacks);
    }

    [Fact]
    public async Task ResetDuringPlayerFetchCancelsTheOldGenerationWithoutPublishingItsLines()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pending = true;
        using var client = Client(async (request, token) =>
        {
            if (pending && request.Method == HttpMethod.Post)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Response(request.Method == HttpMethod.Post ? Player() : Xml);
        });
        using var service = Service(client);
        Task<List<LyricLine>?> old = service.FetchSubtitlesAsync(Video);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Reset();
        Assert.Null(await old);
        pending = false;
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
    }

    [Fact]
    public async Task FetchReplacementRejectsAnOldFallbackThatIgnoredCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldFallback = new TaskCompletionSource<List<LyricLine>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Post ? Player() : "")));
        using var service = new YouTubeSubtitleService(client, (video, _) =>
        {
            if (video != Video) return Task.FromResult<List<LyricLine>?>([new(TimeSpan.Zero, "Latest")]);
            entered.TrySetResult();
            return oldFallback.Task;
        });
        Task<List<LyricLine>?> old = service.FetchSubtitlesAsync(Video);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Latest", Assert.Single((await service.FetchSubtitlesAsync("latest12345"))!).Text);
        oldFallback.SetResult([new(TimeSpan.Zero, "Old")]);
        Assert.Null(await old);
        Assert.Equal("Latest", Assert.Single((await service.FetchSubtitlesAsync("latest12345"))!).Text);
    }

    [Theory]
    [InlineData("watch status")]
    [InlineData("watch error")]
    [InlineData("player status")]
    [InlineData("player error")]
    public async Task PersistentPlayerFailuresReturnNullWithoutNetworkEscapingTheFixture(string failure)
    {
        using var client = Client((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return failure == "player error" ? Task.FromException<HttpResponseMessage>(new HttpRequestException())
                    : Task.FromResult(Response(failure == "player status" ? "no" : "{}", failure == "player status" ? HttpStatusCode.Forbidden : HttpStatusCode.OK));
            return failure == "watch error" ? Task.FromException<HttpResponseMessage>(new HttpRequestException())
                : Task.FromResult(Response("no", HttpStatusCode.NotFound));
        });
        using var service = Service(client);
        Assert.Null(await service.FetchSubtitlesAsync(Video));
    }

    [Theory]
    [InlineData("native,auto,english")]
    [InlineData("bad, English,auto")]
    [InlineData("")]
    public async Task ConfigurationChangesInvalidateCachedLinesAndUnchangedValuesKeepTheCache(string mode)
    {
        int calls = 0;
        using var client = Client((request, _) => { calls++; return Task.FromResult(Response(request.Method == HttpMethod.Post ? Player() : Xml)); });
        using var service = Service(client);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        service.SetMode(mode);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        int changed = calls;
        service.SetMode(mode);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        Assert.Equal(changed, calls);
        service.SetIgnoreAutoGenerated(true);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        Assert.True(calls > changed);
        changed = calls;
        service.SetIgnoreAutoGenerated(true);
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
        Assert.Equal(changed, calls);
        service.SetNativeLanguageHint("vi-VN");
        Assert.NotNull(await service.FetchSubtitlesAsync(Video));
    }

    private static YouTubeSubtitleService Service(HttpClient client) => new(client, (_, _) => Task.FromResult<List<LyricLine>?>(null));
    private static HttpResponseMessage Response(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(content) };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(new Handler(respond));
    private static string Player() => JsonSerializer.Serialize(new
    {
        captions = new
        {
            playerCaptionsTracklistRenderer = new
            {
                captionTracks = new[]
        {
            new { baseUrl = "https://captions.test/en?fixture=1&fmt=srv3", languageCode = "en", vssId = ".en", name = new { simpleText = "English" } }
        }
            }
        }
    });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
