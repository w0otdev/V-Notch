using System.Net;
using System.Net.Http;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class ReleaseFeedIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task PublicReleaseFeedLoadsThroughNativeTransportWhenApiIsUnavailable()
    {
        using var client = new HttpClient(new RateLimitedApiHandler
        {
            InnerHandler = new NativeWinHttpHandler { AllowAutoRedirect = false }
        }) { Timeout = TimeSpan.FromSeconds(30) };
        using var updater = new UpdateService(client, new UpdateSecurityPolicy());
        var releases = await updater.GetAllReleasesAsync();
        Assert.NotEmpty(releases);
        Assert.All(releases, release =>
        {
            Assert.True(UpdateService.TryParseReleaseVersion(release.Version, out _, out _));
            Assert.False(release.IsNewerVersion);
            Assert.Empty(release.DownloadUrl);
        });
        output.WriteLine($"Loaded {releases.Count} live release entries; newest feed entry: {releases[0].Version}.");
    }

    private sealed class RateLimitedApiHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == "api.github.com"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
                : base.SendAsync(request, cancellationToken);
    }
}
