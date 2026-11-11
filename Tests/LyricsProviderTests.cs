using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed partial class LyricsProviderTests
{
    private const string MuxWordPayload = """
        {"meta":{"level":"word","source":{"id":"musixmatch","name":"Musixmatch"}},
         "lines":[{"id":0,"text":"Đi qua một vùng","start":1200,"words":[
            {"text":"Đi","start":1200,"end":1500},
            {"text":"qua","start":1700,"end":2100},
            {"text":"một vùng","start":2300,"end":3100}]}]}
        """;

    private const string NetEaseSearchJson = """
        {"code":200,"result":{"songs":[
          {"id":1001,"name":"Thu Đợi (Live)","dt":310833,"ar":[{"name":"Wren Evans"}]},
          {"id":1002,"name":"Thu Đợi (Remix)","dt":310833,"ar":[{"name":"Wren Evans"}]},
          {"id":1003,"name":"Thu Đợi","dt":285000,"ar":[{"name":"Wren Evans"}]},
          {"id":1004,"name":"Thu Đợi","dt":310833,"ar":[{"name":"Someone Else"}]},
          {"id":3336179095,"name":"Thu Đợi","dt":310833,"ar":[{"name":"Wren Evans"},{"name":"itsnk"}]}
        ]}}
        """;

    private const string NetEaseYrcPayload = """
        {"code":200,"yrc":{"lyric":"[0,1000](0,1000,0) 作词 : Wren Evans\n[1000,1000](1000,1000,0) 作曲 : Wren Evans/itsnk\n[11090,2970](11090,330,0)You (11420,390,0)were (11810,150,0)the\n"}}
        """;

    private const string QqSearchJson = """
        {"code":0,"data":{"song":{"list":[
          {"songmid":"mid_lofi","songname":"Từng Quen (Remix)","interval":174,"singer":[{"name":"Wren Evans"}]},
          {"songmid":"mid_short","songname":"Từng Quen","interval":94,"singer":[{"name":"Wren Evans"}]},
          {"songmid":"000VlkAi0PFHFH","songname":"Từng Quen","interval":174,"singer":[{"name":"Wren Evans"},{"name":"itsnk"}]}
        ]}}}
        """;

    private const string KugouSearchJson = """
        {"status":200,"candidates":[
          {"id":"91","accesskey":"k1","song":"Faded (Cover)","singer":"Alan Walker","duration":212000},
          {"id":"92","accesskey":"k2","song":"Faded","singer":"Other Singer","duration":212000},
          {"id":"54532139","accesskey":"k_exact","song":"Faded","singer":"Alan Walker","duration":212600}
        ]}
        """;

    [Fact]
    public void NetEaseQqAndKugouSelectExactStudioVersionAndRejectWrongEditions()
    {
        using var neDoc = JsonDocument.Parse(NetEaseSearchJson);
        Assert.Equal(3336179095L, LyricsService.SelectBestNetEaseSongId(neDoc.RootElement, "Thu Đợi", "Wren Evans, itsnk", 310));
        Assert.Equal(1001L, LyricsService.SelectBestNetEaseSongId(neDoc.RootElement, "Thu Đợi (Live)", "Wren Evans", 310));
        Assert.Equal(0L, LyricsService.SelectBestNetEaseSongId(neDoc.RootElement, "Thu Đợi", "Wren Evans", 190));

        using var qqDoc = JsonDocument.Parse(QqSearchJson);
        Assert.Equal("000VlkAi0PFHFH", LyricsService.SelectBestQqMusicSongMid(qqDoc.RootElement, "Từng Quen", "Wren Evans, itsnk", 175));
        Assert.Null(LyricsService.SelectBestQqMusicSongMid(qqDoc.RootElement, "Từng Quen", "Wren Evans", 240));

        using var kgDoc = JsonDocument.Parse(KugouSearchJson);
        Assert.Equal(("54532139", "k_exact"), LyricsService.SelectBestKugouCandidate(kgDoc.RootElement, "Faded", "Alan Walker", 212));
    }

    [Fact]
    public void NetEaseParsesYrcWordTimingsAndStripsCreditHeaders()
    {
        using var doc = JsonDocument.Parse(NetEaseYrcPayload);
        var result = LyricsService.ParseNetEaseLyricResult(doc.RootElement);
        Assert.NotNull(result);
        Assert.Equal("NetEase", result.Provider);
        var line = Assert.Single(result.Lines);
        Assert.Equal("You were the", line.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(11090), line.Time);
        Assert.Equal(3, line.Words!.Length);
        Assert.Equal(new LyricWord(TimeSpan.FromMilliseconds(11090), TimeSpan.FromMilliseconds(11420), 0, 4), line.Words[0]);
        Assert.Equal(new LyricWord(TimeSpan.FromMilliseconds(11420), TimeSpan.FromMilliseconds(11810), 4, 5), line.Words[1]);
        Assert.Equal(new LyricWord(TimeSpan.FromMilliseconds(11810), TimeSpan.FromMilliseconds(11960), 9, 3), line.Words[2]);
    }

    [Fact]
    public void KugouDecodesEncryptedKrcAndExtractsWordTimings()
    {
        const string krcSample = "[0,1000]<0,1000,0> 作词 : Alan Walker\n[31609,1753]<0,161,0>You <161,159,0>were <320,193,0>the";
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(Encoding.UTF8.GetBytes(krcSample));
            compressed = ms.ToArray();
        }
        byte[] key = [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];
        byte[] krcBytes = new byte[4 + compressed.Length];
        krcBytes[0] = (byte)'k'; krcBytes[1] = (byte)'r'; krcBytes[2] = (byte)'c'; krcBytes[3] = (byte)'1';
        for (int i = 0; i < compressed.Length; i++)
            krcBytes[i + 4] = (byte)(compressed[i] ^ key[i & 15]);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { content = Convert.ToBase64String(krcBytes) }));
        var result = LyricsService.ParseKugouDownloadResult(doc.RootElement);
        Assert.NotNull(result);
        Assert.Equal("Kugou", result.Provider);
        var line = Assert.Single(result.Lines);
        Assert.Equal("You were the", line.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(31609), line.Time);
        Assert.Equal(3, line.Words!.Length);
        Assert.Equal(new LyricWord(TimeSpan.FromMilliseconds(31609), TimeSpan.FromMilliseconds(31770), 0, 4), line.Words[0]);
        Assert.Equal(new LyricWord(TimeSpan.FromMilliseconds(31770), TimeSpan.FromMilliseconds(31929), 4, 5), line.Words[1]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"text\":\"Missing\",\"start\":1000,\"end\":2000}]")]
    [InlineData("[{\"text\":\"Hello\",\"start\":1000,\"end\":900}]")]
    [InlineData("[{\"text\":5,\"start\":1000,\"end\":2000}]")]
    public void BadWordDataRetainsTheUsableLine(string words)
    {
        byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"lines\":[{\"text\":\"Hello\",\"start\":1000,\"words\":" + words + "}],\"wordMap\":null}");
        var line = Assert.Single(NativeLyricParser.Parse(json, 2));
        Assert.Equal("Hello", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void TopLevelWordMapAndSyllableBoundariesArePreserved()
    {
        byte[] json = """
            {"lines":[{"id":0,"text":"Hello world","start":1000}],"wordMap":[[
             {"text":"Hel","start":1000,"end":1200},{"text":"lo","start":1200,"end":1800},
             {"text":"world","start":2000,"end":2600}]]}
            """u8.ToArray();
        var line = Assert.Single(NativeLyricParser.Parse(json, 2));
        Assert.Equal(3, line.Words!.Length);
        Assert.Equal(3, line.Words[1].Offset);
        Assert.Equal(6, line.Words[2].Offset);
    }

    [Fact]
    public void EnhancedLrcRemovesTagsAndKeepsNativeStarts()
    {
        var line = Assert.Single(LyricsService.ParseLrc("[00:01.20]<00:01.20>Đi <00:01.75>qua <00:02.00>đây"));
        Assert.Equal("Đi qua đây", line.Text);
        Assert.Equal(3, line.Words!.Length);
        Assert.Equal(TimeSpan.FromMilliseconds(1750), line.Words[1].Start);
        Assert.Equal(line.Words[2].Start, line.Words[2].End);
    }

    [Fact]
    public void ProviderOrderRepairsMissingUnknownAndDuplicateValues()
    {
        Assert.Equal(LyricsProviderOrder.Default, string.Join(',', LyricsProviderOrder.Normalize(null)));
        Assert.Equal(new[] { "LRCLIB", "NetEase", "AMLL TTML DB", "Kugou", "QQ Music", "lrc mux" },
            LyricsProviderOrder.Normalize("LRCLIB,unknown, LRCLIB,NetEase"));
        Assert.Equal("NetEase,LRCLIB,AMLL TTML DB,Kugou,QQ Music,lrc mux",
            string.Join(',', LyricsProviderOrder.Normalize("NetEase,LRCLIB")));
    }

    [Fact]
    public async Task ChangingPrioritySeparatesCacheEntriesAndUsesTheFirstSuccessfulSource()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"syncedLyrics\":\"[00:01.00]<00:01.00>Word <00:01.50>sync\"}") };
        }))
        { BaseAddress = new Uri("https://lyrics.test") };
        using var mux = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(MuxWordPayload) }))
        { BaseAddress = new Uri("https://mux.test") };
        using var service = CreateService(client, mux);
        var legacy = await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux");
        var rich = await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "lrc mux,LRCLIB");
        Assert.Equal("LRCLIB", legacy!.Provider);
        Assert.Equal("Musixmatch via lrc mux", rich!.Provider);
        Assert.Equal(1, calls);
        Assert.Same(legacy, await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "LRCLIB,lrc mux"));
    }

    [Fact]
    public async Task UnresponsiveProviderTimesOutFastAndSkipsAcrossSubsequentTracks()
    {
        int hungCalls = 0;
        using var countHung = new HttpClient(new CountingPendingHandler(() => hungCalls++))
        { BaseAddress = new Uri("https://hung.test"), Timeout = TimeSpan.FromMilliseconds(80) };
        using var fallback = new HttpClient(new Handler(req =>
            req.RequestUri!.Query.Contains("Wren%20Evans%2C%20itsnk") ? new(HttpStatusCode.NotFound)
            : new(HttpStatusCode.OK) { Content = new StringContent("{\"syncedLyrics\":\"[00:01.00]Fast failover\"}") }))
        { BaseAddress = new Uri("https://fallback.test") };
        using var service = CreateService(fallback, countHung, netEaseClient: countHung);
        var first = await service.FetchSyncedLyricsAsync("Thu Đợi", "Wren Evans, itsnk", 240, "NetEase,LRCLIB");
        var second = await service.FetchSyncedLyricsAsync("Track 2", "Wren Evans", 240, "NetEase,LRCLIB");
        Assert.Equal("LRCLIB", first!.Provider);
        Assert.Equal("LRCLIB", second!.Provider);
        Assert.Equal(2, hungCalls);
    }

    private sealed class CountingPendingHandler(Action onSend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            onSend();
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task LrcMuxFailureFallsBackWithoutCancellingTheTrack()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("{\"syncedLyrics\":\"[00:01.00]Fallback\"}") }))
        { BaseAddress = new Uri("https://lyrics.test") };
        using var mux = new HttpClient(new Handler(_ => new(HttpStatusCode.TooManyRequests))) { BaseAddress = new Uri("https://mux.test") };
        using var service = CreateService(client, mux);
        Assert.Equal("LRCLIB", (await service.FetchSyncedLyricsAsync("Track", "Artist", 240, "lrc mux,LRCLIB"))!.Provider);
    }

    private static readonly HttpClient Unavailable = new(new Handler(_ => new(HttpStatusCode.NotFound)))
    { BaseAddress = new Uri("https://unavailable.test") };
    private static LyricsService CreateService(HttpClient lrclib, HttpClient mux, HttpClient? netEaseClient = null)
        => new(lrclib, mux, netEaseClient: netEaseClient ?? Unavailable,
            kugouClient: Unavailable, qqMusicClient: Unavailable, amllClient: Unavailable);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }

    [Fact]
    public async Task PreferredProviderCompletesBeforeAnyFallbackIsContacted()
    {
        int fallbackRequests = 0;
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fallback = new HttpClient(new Handler(_ =>
        {
            fallbackRequests++;
            return new(HttpStatusCode.NotFound);
        }))
        { BaseAddress = new Uri("https://fallback.test") };
        using var primary = new HttpClient(new PendingHandler(response.Task)) { BaseAddress = new Uri("https://primary.test") };
        using var service = CreateService(fallback, primary);
        var pending = service.FetchSyncedLyricsAsync("Track", "Artist", 240, "lrc mux,LRCLIB");
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, fallbackRequests);
        response.SetResult(new(HttpStatusCode.OK) { Content = new StringContent(MuxWordPayload) });
        Assert.Equal("Musixmatch via lrc mux", (await pending)!.Provider);
        Assert.Equal(0, fallbackRequests);
    }

    private sealed class PendingHandler(Task<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response.WaitAsync(token);
    }
}
