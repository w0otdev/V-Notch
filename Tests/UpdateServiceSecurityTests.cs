using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UpdateServiceSecurityTests
{
    private const string ReleaseRoot = "https://github.com/rainaku/V-Notch/releases/download/v99.0.0/";
    [Fact]
    public void SelectReleaseAssets_PrefersSelfContained_AndNeverSelectsOtherExe()
    {
        using var doc = JsonDocument.Parse("{\"assets\":[{\"name\":\"malware.exe\",\"browser_download_url\":\"https://example.test/malware.exe\"},{\"name\":\"V-Notch-Setup.exe\",\"browser_download_url\":\"https://example.test/setup\"},{\"name\":\"V-Notch-Setup.exe.sha256\",\"browser_download_url\":\"https://example.test/setup.sha\"},{\"name\":\"V-Notch-Setup-SelfContained.exe\",\"browser_download_url\":\"https://example.test/sc\"},{\"name\":\"V-Notch-Setup-SelfContained.exe.sha256\",\"browser_download_url\":\"https://example.test/sc.sha\"}]}");
        var selected = UpdateService.SelectReleaseAssets(doc.RootElement);
        Assert.Equal("V-Notch-Setup-SelfContained.exe", selected.Installer?.Name);
        Assert.Equal("V-Notch-Setup-SelfContained.exe.sha256", selected.Checksum?.Name);
    }

    [Fact]
    public void SelectReleaseAssets_ReturnsNoInstaller_WhenExactNameMissing()
    {
        using var doc = JsonDocument.Parse("{\"assets\":[{\"name\":\"V-Notch-Setup-evil.exe\",\"browser_download_url\":\"https://example.test/a\"}]}");
        Assert.Null(UpdateService.SelectReleaseAssets(doc.RootElement).Installer);
    }

    [Fact]
    public void HashesMatch_AcceptsExactHash_AndRejectsOneByteChange()
    {
        const string good = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string changed = "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.True(UpdateService.HashesMatch(good, good.ToLowerInvariant()));
        Assert.False(UpdateService.HashesMatch(good, changed));
    }

    [Fact]
    public async Task DownloadInstaller_RejectsHttpRedirect()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://evil.test/setup") } });
        await using var temp = new TempFile();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(ReleaseRoot + "setup", temp.Path, null, default));
    }

    [Fact]
    public async Task DownloadInstaller_RejectsContentOverLimit()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = UpdateSecurityPolicy.MaximumInstallerBytes + 1;
        var service = CreateService(_ => response);
        await using var temp = new TempFile();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(ReleaseRoot + "setup", temp.Path, null, default));
    }

    [Fact]
    public async Task DownloadInstaller_HonorsCancellation()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await using var temp = new TempFile();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadInstallerAsync(ReleaseRoot + "setup", temp.Path, null, cts.Token));
    }

    [Theory]
    [InlineData("https://attackergithubusercontent.com/setup")]
    [InlineData("https://github-production-release-asset-attacker.s3.amazonaws.com/setup")]
    [InlineData("https://example.test/setup")]
    [InlineData("https://github.com/rainaku/V-Notch-evil/releases/download/v99.0.0/setup")]
    [InlineData("https://raw.githubusercontent.com/rainaku/V-Notch/main/setup")]
    public async Task DownloadInstaller_RejectsUnapprovedOriginWithoutMakingRequest(string url)
    {
        bool requested = false;
        var service = CreateService(_ => { requested = true; return new(HttpStatusCode.OK); });
        await using var temp = new TempFile();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(url, temp.Path, null, default));
        Assert.False(requested);
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public async Task DownloadInstaller_RejectsUntrustedHttpsRedirectBeforeContactingTarget()
    {
        int requests = 0;
        var service = CreateService(_ =>
        {
            requests++;
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/setup") } };
        });
        await using var temp = new TempFile();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(ReleaseRoot + "setup", temp.Path, null, default));
        Assert.Equal(1, requests);
        Assert.False(File.Exists(temp.Path));
    }

    [Theory]
    [InlineData("objects.githubusercontent.com")]
    [InlineData("release-assets.githubusercontent.com")]
    public async Task DownloadInstaller_AcceptsGitHubReleaseCdnRedirect(string host)
    {
        int requests = 0;
        var service = CreateService(request =>
        {
            requests++;
            return request.RequestUri!.Host == "github.com"
                ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri($"https://{host}/asset") } }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        await using var temp = new TempFile();
        await service.DownloadInstallerAsync(ReleaseRoot + "setup", temp.Path, null, default);
        Assert.Equal(2, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(temp.Path));
    }

    [Fact]
    public async Task RedirectsPreserveRepresentationHeadersWithoutForwardingCredentials()
    {
        int requests = 0;
        var service = CreateService(request =>
        {
            requests++;
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.GetValues("Accept")));
            Assert.Equal("\"release-etag\"", Assert.Single(request.Headers.GetValues("If-None-Match")));
            Assert.Equal(requests == 1, request.Headers.Contains("Authorization"));
            Assert.Equal(requests == 1, request.Headers.Contains("Cookie"));
            Assert.Equal(requests == 1, request.Headers.Host != null);
            return requests switch
            {
                1 => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/moved", UriKind.Relative) } },
                2 => new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset") } },
                _ => new(HttpStatusCode.OK)
            };
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/releases");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"release-etag\"");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-only");
        request.Headers.TryAddWithoutValidation("Cookie", "test-only=value");
        request.Headers.Host = "api.github.com";
        using var response = await service.SendHttpsAsync(request, default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task ReleaseEndpointsParseStreamsWithoutBufferingContentAsAString()
    {
        const string release = "{\"tag_name\":\"v99.0.0\",\"body\":\"notes\",\"published_at\":\"2026-10-04T00:00:00Z\",\"assets\":[]}";
        var service = CreateService(request => new(HttpStatusCode.OK)
        {
            Content = new StreamOnlyContent(request.RequestUri!.AbsolutePath.EndsWith("/latest") ? release : "[" + release + "]")
        });
        Assert.Equal("99.0.0", (await service.CheckForUpdatesAsync())?.Version);
        Assert.Equal("99.0.0", Assert.Single(await service.GetAllReleasesAsync()).Version);
    }

    [Fact]
    public async Task CheckForUpdates_InvalidReleaseJson_ReturnsNull()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });
        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Theory]
    [InlineData("1.8.0", "1.7.9", 1)]
    [InlineData("1.7.0", "1.7.0", 0)]
    [InlineData("1.7.0", "1.7.1", -1)]
    [InlineData("invalid", "1.7.0", 0)]
    public void CompareVersions_HandlesValidAndInvalidValues(string left, string right, int expectedSign)
    {
        var result = UpdateService.CompareVersions(left, right);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    private static UpdateService CreateService(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new HttpClient(new StubHandler(reply)), new UpdateSecurityPolicy(), _ => (true, string.Empty));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
    private sealed class BlockingStream : MemoryStream
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromCanceled<int>(token); }
    private sealed class StreamOnlyContent(string json) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Content must be consumed as a stream.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class TempFile : IAsyncDisposable
    { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")); public ValueTask DisposeAsync() { if (File.Exists(Path)) File.Delete(Path); return ValueTask.CompletedTask; } }
}
