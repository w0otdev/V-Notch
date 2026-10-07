using System.Net;
using System.Net.Http;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LyricsFetchTests
{
    [Fact]
    public async Task RepeatedTracksAndReturningToPreviousTracksReuseLyrics()
    {
        int requests = 0;
        using var client = Client((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Lyrics());
        });
        using var service = new LyricsService(client, client);
        var first = await service.FetchSyncedLyricsAsync("Track A", "Artist", 240);
        Assert.NotNull(first);
        Assert.Same(first, await service.FetchSyncedLyricsAsync("Track A", "Artist", 240));
        Assert.NotNull(await service.FetchSyncedLyricsAsync("Track B", "Artist", 240));
        Assert.Same(first, await service.FetchSyncedLyricsAsync("Track A", "Artist", 240));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneTrackShareThePendingResult()
    {
        int requests = 0;
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => { Interlocked.Increment(ref requests); return response.Task; });
        using var service = new LyricsService(client, client);
        var first = service.FetchSyncedLyricsAsync("Track", "Artist", 240);
        var duplicates = Enumerable.Range(0, 20).Select(_ => service.FetchSyncedLyricsAsync("Track", "Artist", 240)).ToArray();
        Assert.All(duplicates, duplicate => Assert.Same(first, duplicate));
        response.SetResult(Lyrics());
        var result = await first.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(result);
        foreach (var duplicate in duplicates) Assert.Same(result, await duplicate);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SwitchingTracksCancelsTheOldFetchAndReturningToCacheCancelsPendingWork()
    {
        CancellationToken pendingToken = default;
        using var client = Client(async (request, token) =>
        {
            if (request.RequestUri!.Query.Contains("Pending"))
            {
                pendingToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Lyrics();
        });
        using var service = new LyricsService(client, client);
        var cached = await service.FetchSyncedLyricsAsync("Cached", "Artist", 240);
        var pending = service.FetchSyncedLyricsAsync("Pending", "Artist", 240);
        Assert.Same(cached, await service.FetchSyncedLyricsAsync("Cached", "Artist", 240));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(pendingToken.IsCancellationRequested);
        Assert.NotNull(await service.FetchSyncedLyricsAsync("Latest", "Artist", 240));
    }

    [Fact]
    public async Task ResetAllowsRetryWithoutDiscardingCompletedLyrics()
    {
        int requests = 0;
        using var client = Client(async (_, token) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Lyrics();
        });
        using var service = new LyricsService(client, client);
        var pending = service.FetchSyncedLyricsAsync("Track", "Artist", 240);
        service.Reset();
        var retry = await service.FetchSyncedLyricsAsync("Track", "Artist", 240);
        Assert.NotNull(retry);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(60)));
        service.Reset();
        Assert.Same(retry, await service.FetchSyncedLyricsAsync("Track", "Artist", 240));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task FailedRequestsCanRetryTheSameTrack()
    {
        int requests = 0;
        using var client = Client((_, _) => ++requests == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline fixture"))
            : Task.FromResult(Lyrics()));
        using var service = new LyricsService(client, client);
        Assert.Null(await service.FetchSyncedLyricsAsync("Track", "Artist", 240));
        Assert.NotNull(await service.FetchSyncedLyricsAsync("Track", "Artist", 240));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task CacheEvictsTheLeastRecentlyUsedTrackAndSeparatesDurations()
    {
        int requests = 0;
        using var client = Client((_, _) => { requests++; return Task.FromResult(Lyrics()); });
        using var service = new LyricsService(client, client, cacheCapacity: 2);
        var first = await service.FetchSyncedLyricsAsync("A", "Artist", 240);
        await service.FetchSyncedLyricsAsync("B", "Artist", 240);
        Assert.Same(first, await service.FetchSyncedLyricsAsync("A", "Artist", 240));
        await service.FetchSyncedLyricsAsync("C", "Artist", 240);
        Assert.Same(first, await service.FetchSyncedLyricsAsync("A", "Artist", 240));
        await service.FetchSyncedLyricsAsync("B", "Artist", 240);
        Assert.Equal(4, requests);
        await service.FetchSyncedLyricsAsync("A", "Artist", 241);
        Assert.Equal(5, requests);
    }

    [Fact]
    public async Task DisposeDuringFetchIsSafeAndIdempotent()
    {
        using var client = Client(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Lyrics();
        });
        using var service = new LyricsService(client, client);
        var pending = service.FetchSyncedLyricsAsync("Track", "Artist", 240);
        Parallel.For(0, 20, _ => { service.Reset(); service.Dispose(); });
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Null(await service.FetchSyncedLyricsAsync("Track", "Artist", 240));
    }

    [Fact]
    public async Task RapidTrackChangesCompleteWithoutCancellationSourceRaces()
    {
        using var client = Client(async (_, token) =>
        {
            await Task.Delay(10, token);
            return Lyrics();
        });
        using var service = new LyricsService(client, client);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(
            () => service.FetchSyncedLyricsAsync("Track " + index, "Artist", 240)))).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(results, result => result is { Lines.Count: 2 });
        Assert.NotNull(await service.FetchSyncedLyricsAsync("Final", "Artist", 240));
    }

    private static HttpResponseMessage Lyrics() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"syncedLyrics\":\"[00:01.00]First line\\n[00:02.00]Second line\"}")
    };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new Handler(handler)) { BaseAddress = new Uri("https://lyrics.test") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
