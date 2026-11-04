using System.Net;
using System.Net.Http;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ProgressiveLyricsTests
{
    internal const string WordPayload = """
        {"meta":{"level":"word"},"lines":[{"text":"Hello world","start":1000,"words":[
        {"text":"Hello","start":1000,"end":1500},{"text":"world","start":1600,"end":2000}]}]}
        """;
    private static HttpResponseMessage Response(string text)
        => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task LinePreviewIsImmediateAndJoiningPrefetchReplaysItBeforeWordUpgrade()
    {
        var words = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var line = Client(_ => Task.FromResult(Response("{\"syncedLyrics\":\"[00:01.00]Hello world\"}")));
        using var rich = Client(token => words.Task.WaitAsync(token));
        using var unavailable = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = new LyricsService(line, rich, netEaseClient: unavailable, kugouClient: unavailable, qqMusicClient: unavailable);
        var previews = new List<LyricsResult>();
        var pending = service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux", onLyricsAvailable: previews.Add);
        Assert.False(pending.IsCompleted);
        Assert.Equal("LRCLIB", Assert.Single(previews).Provider);
        var replay = new List<LyricsResult>();
        Assert.Same(pending, service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux", onLyricsAvailable: replay.Add));
        Assert.Same(previews[0], Assert.Single(replay));
        words.SetResult(Response(WordPayload));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result!.Lines[0].Words);
        Assert.Same(result, await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux"));
    }

    [Fact]
    public async Task ExhaustionRetainsFirstLineSourceAndEndsTheSearch()
    {
        int attempts = 0;
        using var line = Client(_ => Task.FromResult(Response("{\"syncedLyrics\":\"[00:01.00]Hello world\"}")));
        using var unavailable = Client(_ => { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
        using var service = new LyricsService(line, unavailable, netEaseClient: unavailable, kugouClient: unavailable, qqMusicClient: unavailable);
        LyricsResult? preview = null;
        var result = await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux", onLyricsAvailable: value => preview = value)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(preview, result);
        Assert.Null(result!.Lines[0].Words);
        int completedAttempts = attempts;
        Assert.True(completedAttempts > 0);
        Assert.Same(result, await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux"));
        Assert.Equal(completedAttempts, attempts);
    }

    [Fact]
    public async Task ResetCancelsTheWordSearchAfterAPreview()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var line = Client(_ => Task.FromResult(Response("{\"syncedLyrics\":\"[00:01.00]Hello world\"}")));
        using var hung = Client(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { canceled.TrySetResult(); }
            return new(HttpStatusCode.NotFound);
        });
        using var service = new LyricsService(line, hung);
        LyricsResult? preview = null;
        var pending = service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux", onLyricsAvailable: value => preview = value);
        Assert.NotNull(preview);
        service.Reset();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static HttpClient Client(Func<CancellationToken, Task<HttpResponseMessage>> response)
        => new(new Handler(response)) { BaseAddress = new Uri("https://lyrics.test") };
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(token);
    }
}
