using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AmllLyricsTests
{
    private const string TrackId = "2dkIbR4mudsHg45av3YwxQ";
    private const string OtherId = "3KYs6A8fOWMEX0sjYxA4gY";
    private const string Ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <body><div begin="1s" end="10s">
            <p begin="00:01.200" end="00:04.500"><span begin="1.2s" end="1.5s">Đi</span> <span begin="1.7s" end="2.1s">qua</span><span begin="0" end="0"> </span><span begin="2.3s" end="3.1s">đây🎵</span><span ttm:role="x-translation">Translation</span><span ttm:role="x-roman">Roman</span><span ttm:role="x-bg"><span begin="1.2s" end="2s">(Backing)</span></span></p>
            <p begin="5s" end="6s">Next line</p>
          </div></body>
        </tt>
        """;

    [Fact]
    public void TtmlPreservesAbsoluteSyllableTimesSpacesAndUtf16Offsets()
    {
        var result = LyricsService.ParseAmllTtml(Ttml)!;
        Assert.Equal(LyricsProviderOrder.Amll, result.Provider);
        Assert.Equal(2, result.Lines.Count);
        var line = result.Lines[0];
        Assert.Equal("Đi qua đây🎵", line.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), line.Time);
        Assert.Equal(new[] {
            new LyricWord(TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1500), 0, 2),
            new LyricWord(TimeSpan.FromMilliseconds(1700), TimeSpan.FromMilliseconds(2100), 3, 3),
            new LyricWord(TimeSpan.FromMilliseconds(2300), TimeSpan.FromMilliseconds(3100), 7, 5)
        }, line.Words);
        Assert.Null(result.Lines[1].Words);
    }

    [Theory]
    [InlineData("00:01:02.5", 62500)]
    [InlineData("01:02.55", 62550)]
    [InlineData("95", 95000)]
    [InlineData("15.8s", 15800)]
    public void TtmlAcceptsAmllClockAndSecondsFormats(string begin, int ms)
    {
        var result = LyricsService.ParseAmllTtml($"<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><p begin=\"{begin}\" end=\"100s\">Line</p></body></tt>");
        Assert.Equal(TimeSpan.FromMilliseconds(ms), Assert.Single(result!.Lines).Time);
    }

    [Theory]
    [InlineData("end=\"1s\"")]
    [InlineData("begin=\"2s\" end=\"1s\"")]
    [InlineData("begin=\"NaN\" end=\"2s\"")]
    [InlineData("begin=\"0s\" end=\"2s\"")]
    public void InvalidWordTimingKeepsTheUsableLine(string timing)
    {
        string xml = $"<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><p begin=\"1s\" end=\"3s\"><span {timing}>Hello</span></p></body></tt>";
        var line = Assert.Single(LyricsService.ParseAmllTtml(xml)!.Lines);
        Assert.Equal("Hello", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void XmlExternalEntitiesAreRejected()
        => Assert.Throws<XmlException>(() => LyricsService.ParseAmllTtml("<!DOCTYPE tt [<!ENTITY x SYSTEM 'file:///not-read'>]><tt>&x;</tt>"));

    [Theory]
    [InlineData(TrackId)]
    [InlineData("spotify:track:" + TrackId)]
    [InlineData("https://open.spotify.com/track/" + TrackId + "?si=abc")]
    [InlineData("https://open.spotify.com/intl-vi/track/" + TrackId)]
    public void SpotifyIdsAndTrackLinksNormalize(string value)
        => Assert.Equal(TrackId, LyricsService.NormalizeSpotifyTrackId(value));

    [Theory]
    [InlineData(null)]
    [InlineData("../../arbitrary")]
    [InlineData("https://evil.test/track/" + TrackId)]
    [InlineData("https://open.spotify.com/album/" + TrackId)]
    [InlineData("spotify:episode:" + TrackId)]
    [InlineData("2dkIbR4mudsHg45av3Ywx/")]
    public void InvalidIdsCannotBecomeRawPaths(string? value)
        => Assert.Null(LyricsService.NormalizeSpotifyTrackId(value));

    [Fact]
    public async Task PrimaryUsesOnlyTheRawTrackFileAndCachesPerId()
    {
        var paths = new List<string>();
        using var raw = Client(request =>
        {
            Assert.Null(request.Headers.Authorization);
            paths.Add(request.RequestUri!.AbsolutePath);
            return Reply(Ttml);
        });
        using var fallback = Client(_ => throw new InvalidOperationException("Fallback should not be contacted"));
        using var service = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw);
        var first = await service.FetchSyncedLyricsAsync("Same title", "Artist", 240, LyricsProviderOrder.Default, TrackId);
        Assert.Equal(LyricsProviderOrder.Amll, first!.Provider);
        Assert.Same(first, await service.FetchSyncedLyricsAsync("Same title", "Artist", 240, LyricsProviderOrder.Default, TrackId));
        Assert.NotSame(first, await service.FetchSyncedLyricsAsync("Same title", "Artist", 240, LyricsProviderOrder.Default, OtherId));
        Assert.Equal(new[] {
            $"/amll-dev/amll-ttml-db/main/spotify-lyrics/{TrackId}.ttml",
            $"/amll-dev/amll-ttml-db/main/spotify-lyrics/{OtherId}.ttml"
        }, paths);
    }

    [Theory]
    [InlineData(404, "", 2)]
    [InlineData(429, "", 1)]
    [InlineData(503, "", 1)]
    [InlineData(200, "<broken", 2)]
    [InlineData(200, "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body/></tt>", 2)]
    public async Task MissingOrUnusableFilesFallBackAndOnlyOutagesCoolDown(int status, string payload, int expectedCalls)
    {
        int rawCalls = 0;
        using var raw = Client(_ => { rawCalls++; return Reply(payload, (HttpStatusCode)status); });
        using var fallback = Client(_ => Reply("{\"syncedLyrics\":\"[00:01.00]Fallback\"}"));
        using var service = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw);
        foreach (string id in new[] { TrackId, OtherId })
            Assert.Equal("LRCLIB", (await service.FetchSyncedLyricsAsync("Track", "Artist", 240, LyricsProviderOrder.Default, id))!.Provider);
        Assert.Equal(expectedCalls, rawCalls);
    }

    [Fact]
    public async Task MissingIdSkipsRawWhileLocalSnapshotCanSupplyItWithoutAKey()
    {
        int rawCalls = 0;
        using var raw = Client(_ => { rawCalls++; return Reply(Ttml); });
        using var fallback = Client(_ => Reply("{\"syncedLyrics\":\"[00:01.00]Fallback\"}"));
        using (var service = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw))
            Assert.Equal("LRCLIB", (await service.FetchSyncedLyricsAsync("Track", "Artist", 240, LyricsProviderOrder.Default))!.Provider);
        Assert.Equal(0, rawCalls);
        var local = new SpotifyLocalMetadataReader(() => new("Track", "Artist", TrackId));
        using var withLocal = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw, spotifyLocalMetadata: local);
        Assert.Equal(LyricsProviderOrder.Amll, (await withLocal.FetchSyncedLyricsAsync("Track", "Artist", 240, LyricsProviderOrder.Default))!.Provider);
        Assert.Equal(1, rawCalls);
    }

    [Fact]
    public async Task HungRawResponseTimesOutAndDoesNotDelayTheNextTrack()
    {
        int calls = 0;
        using var raw = new HttpClient(new AsyncHandler(async token =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            return Reply(Ttml);
        }))
        { BaseAddress = new Uri("https://raw.test"), Timeout = TimeSpan.FromMilliseconds(80) };
        using var fallback = Client(_ => Reply("{\"syncedLyrics\":\"[00:01.00]Fallback\"}"));
        using var service = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw);
        foreach (string id in new[] { TrackId, OtherId })
            Assert.Equal("LRCLIB", (await service.FetchSyncedLyricsAsync("Track", "Artist", 240, LyricsProviderOrder.Default, id))!.Provider);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SupersededRawRequestCannotPublishOrCacheItsLyrics()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var raw = new HttpClient(new AsyncHandler(async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reply(Ttml);
        }))
        { BaseAddress = new Uri("https://raw.test") };
        using var fallback = Client(_ => Reply("{\"syncedLyrics\":\"[00:01.00]Fallback\"}"));
        using var service = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw);
        var previous = service.FetchSyncedLyricsAsync("Track", "Artist", 240, LyricsProviderOrder.Default, TrackId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var next = service.FetchSyncedLyricsAsync("Next", "Artist", 240, LyricsProviderOrder.Default);
        Assert.Null(await previous.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("LRCLIB", (await next)!.Provider);
    }

    [Fact]
    public async Task OversizedBodyFallsBackWithoutParsing()
    {
        using var raw = Client(_ => Reply(new string('x', 2 * 1024 * 1024 + 1)));
        using var fallback = Client(_ => Reply("{\"syncedLyrics\":\"[00:01.00]Fallback\"}"));
        using var service = new LyricsService(fallback, fallback, netEaseClient: fallback, kugouClient: fallback, qqMusicClient: fallback, amllClient: raw);
        Assert.Equal("LRCLIB", (await service.FetchSyncedLyricsAsync("Track", "Artist", 240, LyricsProviderOrder.Default, TrackId))!.Provider);
    }

    [Fact]
    public void ExistingDefaultMigratesButCustomPriorityRemainsInOrder()
    {
        Assert.Equal(LyricsProviderOrder.Default, string.Join(',', LyricsProviderOrder.Normalize("LRCLIB,NetEase,lrc mux")));
        Assert.Equal("NetEase,LRCLIB,lrc mux,AMLL TTML DB,Kugou,QQ Music", string.Join(',', LyricsProviderOrder.Normalize("NetEase,LRCLIB,lrc mux")));
    }

    [Fact]
    public async Task LocalTrackIdRefreshesEvenWhenTitleAndArtistStayTheSame()
    {
        int reads = 0;
        var local = new SpotifyLocalMetadataReader(() => new("Track", "Artist", ++reads == 1 ? TrackId : OtherId));
        Assert.Equal(TrackId, await local.TryGetTrackIdAsync("Track", "Artist", default));
        Assert.Equal(OtherId, await local.TryGetTrackIdAsync("Track", "Artist", default));
    }

    private static HttpResponseMessage Reply(string content, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(content, Encoding.UTF8) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply)
        => new(new Handler(reply)) { BaseAddress = new Uri("https://lyrics.test") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(reply(request));
    }
    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(token);
    }
}
