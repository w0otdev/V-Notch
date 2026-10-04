using System.Net;
using System.Net.Http;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyQueryMetadataProviderTests
{
    private static readonly string CatalogHash = new('a', 64);
    private static readonly string CanvasHash = new('b', 64);
    private const string MobileBundle = "https://open.spotifycdn.com/cdn/build/mobile-web-player/mobile-web-player.abc123.js";
    private const string DesktopBundle = "https://open.spotifycdn.com/cdn/build/web-player/web-player.def456.js";
    private static string Metadata => $"'findTracks','query','{CatalogHash}';'canvas','query','{CanvasHash}'";

    [Fact]
    public async Task ConcurrentRefreshes_ReadOneBundleForBothHashes()
    {
        int pages = 0;
        int scripts = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "open.spotify.com")
            {
                Interlocked.Increment(ref pages);
                return Response($"<script src='{MobileBundle}'></script>");
            }
            Interlocked.Increment(ref scripts);
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Response(Metadata);
        }));
        using var metadata = new SpotifyQueryMetadataProvider(http);
        string oldCanvas = metadata.CanvasHash;

        Task<string?> catalog = metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string?> canvas = metadata.RefreshAsync(SpotifyQuery.Canvas, oldCanvas, CancellationToken.None);
        release.SetResult();

        Assert.Equal(new[] { CatalogHash, CanvasHash }, await Task.WhenAll(catalog, canvas));
        Assert.Equal(1, pages);
        Assert.Equal(1, scripts);
    }

    [Fact]
    public async Task RejectedHash_ReusesMetadataFromTheSameBundleUrl()
    {
        int scripts = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "open.spotify.com")
                return Task.FromResult(Response($"<script src='{MobileBundle}'></script>"));
            scripts++;
            return Task.FromResult(Response(Metadata));
        }));
        using var metadata = new SpotifyQueryMetadataProvider(http);

        Assert.Equal(CatalogHash, await metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, CancellationToken.None));
        Assert.Equal(CatalogHash, await metadata.RefreshAsync(SpotifyQuery.FindTracks, CatalogHash, CancellationToken.None));
        Assert.Equal(1, scripts);
    }

    [Fact]
    public async Task SeparateBundles_UseTheMatchingPlayerWhenTheOtherHashIsAbsent()
    {
        var scripts = new List<Uri>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "open.spotify.com")
            {
                string bundle = request.Headers.UserAgent.ToString().Contains("Mobile", StringComparison.Ordinal)
                    ? MobileBundle : DesktopBundle;
                return Task.FromResult(Response($"<script src='{bundle}'></script>"));
            }
            scripts.Add(request.RequestUri);
            string body = request.RequestUri.AbsoluteUri == MobileBundle
                ? $"'findTracks','query','{CatalogHash}'" : $"'canvas','query','{CanvasHash}'";
            return Task.FromResult(Response(body));
        }));
        using var metadata = new SpotifyQueryMetadataProvider(http);

        Assert.Equal(CatalogHash, await metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, CancellationToken.None));
        Assert.Equal(CanvasHash, await metadata.RefreshAsync(SpotifyQuery.Canvas, metadata.CanvasHash, CancellationToken.None));
        Assert.Equal(new[] { new Uri(MobileBundle), new Uri(DesktopBundle) }, scripts);
    }

    [Fact]
    public async Task CancelledRefresh_ReleasesTheLockAndAllowsRetry()
    {
        int scripts = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "open.spotify.com")
                return Response($"<script src='{MobileBundle}'></script>");
            if (++scripts == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response(Metadata);
        }));
        using var metadata = new SpotifyQueryMetadataProvider(http);
        using var cts = new CancellationTokenSource();
        Task<string?> pending = metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(CatalogHash, await metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, CancellationToken.None));
        Assert.Equal(2, scripts);
    }

    [Fact]
    public async Task FailedDownload_IsNotCached()
    {
        int scripts = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "open.spotify.com")
                return Task.FromResult(Response($"<script src='{MobileBundle}'></script>"));
            return Task.FromResult(++scripts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(Metadata));
        }));
        using var metadata = new SpotifyQueryMetadataProvider(http);

        Assert.Null(await metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, CancellationToken.None));
        Assert.Equal(CatalogHash, await metadata.RefreshAsync(SpotifyQuery.FindTracks, metadata.FindTracksHash, CancellationToken.None));
        Assert.Equal(2, scripts);
    }

    [Fact]
    public async Task OversizedBundle_DoesNotPublishItsHashes()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "open.spotify.com")
                return Task.FromResult(Response($"<script src='{MobileBundle}'></script>"));
            HttpResponseMessage response = Response(Metadata);
            response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        }));
        using var metadata = new SpotifyQueryMetadataProvider(http);
        string original = metadata.FindTracksHash;

        Assert.Null(await metadata.RefreshAsync(SpotifyQuery.FindTracks, original, CancellationToken.None));
        Assert.Equal(original, metadata.FindTracksHash);
    }

    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8)
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
