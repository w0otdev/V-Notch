using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class YouTubeQuotaCredentialTests
{
    [Fact]
    public async Task ReplacingAQuotaExhaustedKeyResumesSearchAndMetadataRequests()
    {
        string oldKey = NewKey(), replacementKey = NewKey(), currentKey = oldKey;
        string videoId = NewVideoId();
        const string title = "Replacement credential song";
        using var keyHandler = new Handler((request, _) =>
        {
            if (ApiKey(request) == oldKey) return Task.FromResult(QuotaExceeded());
            var response = request.RequestUri!.AbsolutePath.EndsWith("/search")
                ? Json(new { items = new[] { new { id = new { videoId }, snippet = new { title } } } })
                : VideoMetadata(title);
            return Task.FromResult(response);
        });
        using var fallbackHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var keyClient = new HttpClient(keyHandler);
        using var fallbackClient = new HttpClient(fallbackHandler);
        var service = new MediaMetadataLookupService(fallbackClient, keyClient, () => true, () => Volatile.Read(ref currentKey));

        Assert.Null(await service.TrySearchYouTubeByTitleAsync(title));
        Assert.Single(keyHandler.Requests);
        Interlocked.Exchange(ref currentKey, replacementKey);

        var result = await service.TrySearchYouTubeByTitleAsync(title);

        Assert.NotNull(result);
        Assert.Equal(videoId, result.Id);
        Assert.Equal(YouTubeLookupSource.DataApi, result.Source);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Duration);
        Assert.Collection(keyHandler.Requests,
            request => { Assert.Equal(oldKey, request.Key); Assert.EndsWith("/search", request.Uri.AbsolutePath); },
            request => { Assert.Equal(replacementKey, request.Key); Assert.EndsWith("/search", request.Uri.AbsolutePath); },
            request => { Assert.Equal(replacementKey, request.Key); Assert.EndsWith("/videos", request.Uri.AbsolutePath); });
    }

    [Fact]
    public async Task VideoQuotaSuppressesSubsequentMetadataAndDurationEnrichmentRequests()
    {
        string key = NewKey();
        using var keyHandler = new Handler((_, _) => Task.FromResult(QuotaExceeded()));
        using var fallbackHandler = new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/oembed"
                ? Json(new { title = "Fallback song", author_name = "Artist" })
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var keyClient = new HttpClient(keyHandler);
        using var fallbackClient = new HttpClient(fallbackHandler);
        var service = new MediaMetadataLookupService(fallbackClient, keyClient, () => true, () => key);

        var first = await service.TryGetYouTubeVideoIdWithInfoAsync(NewVideoId());
        var second = await service.TryGetYouTubeVideoIdWithInfoAsync(NewVideoId());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(YouTubeLookupSource.OEmbed, first.Source);
        Assert.Equal(YouTubeLookupSource.OEmbed, second.Source);
        Assert.Equal(TimeSpan.Zero, first.Duration);
        var request = Assert.Single(keyHandler.Requests);
        Assert.Contains("snippet", request.Uri.Query);
        Assert.Equal(2, fallbackHandler.Requests.Count);
    }

    [Fact]
    public async Task ReplacingKeyRefreshesTheSameVideoInsteadOfReusingOldFallbackCache()
    {
        string oldKey = NewKey(), replacementKey = NewKey(), currentKey = oldKey;
        string videoId = NewVideoId();
        using var keyHandler = new Handler((request, _) => Task.FromResult(
            ApiKey(request) == oldKey ? QuotaExceeded() : VideoMetadata("Restored metadata")));
        using var fallbackHandler = new Handler((_, _) => Task.FromResult(Json(new { title = "Old fallback", author_name = "Artist" })));
        using var keyClient = new HttpClient(keyHandler);
        using var fallbackClient = new HttpClient(fallbackHandler);
        var service = new MediaMetadataLookupService(fallbackClient, keyClient, () => true, () => Volatile.Read(ref currentKey));

        var oldResult = await service.TryGetYouTubeVideoIdWithInfoAsync(videoId);
        Interlocked.Exchange(ref currentKey, replacementKey);
        var replacementResult = await service.TryGetYouTubeVideoIdWithInfoAsync(videoId);

        Assert.NotNull(oldResult);
        Assert.NotNull(replacementResult);
        Assert.Equal(YouTubeLookupSource.OEmbed, oldResult.Source);
        Assert.Equal(TimeSpan.Zero, oldResult.Duration);
        Assert.Equal(YouTubeLookupSource.DataApi, replacementResult.Source);
        Assert.Equal(TimeSpan.FromMinutes(2), replacementResult.Duration);
        Assert.Equal("Restored metadata", replacementResult.Title);
        Assert.Equal(2, keyHandler.Requests.Count);
        Assert.Single(fallbackHandler.Requests);
    }

    [Fact]
    public async Task LateQuotaResponseForOldKeyDoesNotSuppressReplacementCredential()
    {
        string oldKey = NewKey(), replacementKey = NewKey(), currentKey = oldKey;
        string videoId = NewVideoId();
        var oldRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var keyHandler = new Handler(async (request, token) =>
        {
            if (ApiKey(request) == oldKey)
            {
                oldRequestStarted.TrySetResult();
                return await oldResponse.Task.WaitAsync(token);
            }
            return VideoMetadata("Replacement song");
        });
        using var fallbackHandler = new Handler((_, _) => Task.FromResult(Json(new { title = "Old fallback song", author_name = "Artist" })));
        using var keyClient = new HttpClient(keyHandler);
        using var fallbackClient = new HttpClient(fallbackHandler);
        var service = new MediaMetadataLookupService(fallbackClient, keyClient, () => true, () => Volatile.Read(ref currentKey));

        var oldLookup = service.TryGetYouTubeVideoIdWithInfoAsync(videoId);
        await oldRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref currentKey, replacementKey);
        var beforeOldResponse = await service.TryGetYouTubeVideoIdWithInfoAsync(videoId);
        oldResponse.TrySetResult(QuotaExceeded());
        var oldResult = await oldLookup;
        var cachedAfterOldResponse = await service.TryGetYouTubeVideoIdWithInfoAsync(videoId);
        var afterOldResponse = await service.TryGetYouTubeVideoIdWithInfoAsync(NewVideoId());

        Assert.NotNull(oldResult);
        Assert.Equal(YouTubeLookupSource.OEmbed, oldResult.Source);
        Assert.NotNull(beforeOldResponse);
        Assert.NotNull(cachedAfterOldResponse);
        Assert.NotNull(afterOldResponse);
        Assert.Equal(YouTubeLookupSource.DataApi, beforeOldResponse.Source);
        Assert.Same(beforeOldResponse, cachedAfterOldResponse);
        Assert.Equal("Replacement song", cachedAfterOldResponse.Title);
        Assert.Equal(YouTubeLookupSource.DataApi, afterOldResponse.Source);
        Assert.Equal(TimeSpan.FromMinutes(2), afterOldResponse.Duration);
        Assert.Equal(1, keyHandler.Requests.Count(request => request.Key == oldKey));
        Assert.Equal(2, keyHandler.Requests.Count(request => request.Key == replacementKey));
    }

    [Fact]
    public async Task QuotaOnDurationEnrichmentAlsoSuppressesLaterApiRequests()
    {
        string key = NewKey();
        using var keyHandler = new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains("snippet", StringComparison.Ordinal)
                ? Json(new { items = Array.Empty<object>() })
                : QuotaExceeded()));
        using var fallbackHandler = new Handler((_, _) => Task.FromResult(Json(new { title = "Fallback song", author_name = "Artist" })));
        using var keyClient = new HttpClient(keyHandler);
        using var fallbackClient = new HttpClient(fallbackHandler);
        var service = new MediaMetadataLookupService(fallbackClient, keyClient, () => true, () => key);

        var first = await service.TryGetYouTubeVideoIdWithInfoAsync(NewVideoId());
        Assert.NotNull(first);
        Assert.Equal(2, keyHandler.Requests.Count); // Metadata miss, then duration quota.

        var second = await service.TryGetYouTubeVideoIdWithInfoAsync(NewVideoId());

        Assert.NotNull(second);
        Assert.Equal(YouTubeLookupSource.OEmbed, second.Source);
        Assert.Equal(2, keyHandler.Requests.Count);
    }

    private static string NewKey() => "quota-test-" + Guid.NewGuid().ToString("N");
    private static string NewVideoId() => Guid.NewGuid().ToString("N")[..11];
    private static string ApiKey(HttpRequestMessage request) => request.Headers.GetValues("x-goog-api-key").Single();
    private static HttpResponseMessage QuotaExceeded() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("{\"error\":{\"errors\":[{\"reason\":\"quotaExceeded\"}]}}")
    };
    private static HttpResponseMessage VideoMetadata(string title) => Json(new
    {
        items = new[] { new { snippet = new { title, channelTitle = "Artist" }, contentDetails = new { duration = "PT2M" } } }
    });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value))
    };

    private sealed record RequestInfo(Uri Uri, string? Key);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<RequestInfo> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.RequestUri!, request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null));
            return send(request, cancellationToken);
        }
    }
}
