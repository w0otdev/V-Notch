using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VNotch.Presenters;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UpdateFeedFallbackTests
{
    [Theory]
    [InlineData(false, "99.1.0")]
    [InlineData(true, "99.2.0-beta.1")]
    public async Task RateLimitedChecksUseSignedFeedAssetsAndRespectTheSelectedChannel(bool betas, string expected)
    {
        using var fixture = new Fixture("99.1.0", "99.2.0-beta.1");
        fixture.Sign("99.1.0", UpdateService.SetupName);
        fixture.Sign("99.2.0-beta.1", UpdateService.SelfContainedSetupName);
        using var service = fixture.Service();
        var update = await service.CheckForUpdatesAsync(betas);
        Assert.NotNull(update);
        Assert.Equal(expected, update.Version);
        Assert.True(update.IsNewerVersion);
        Assert.Equal(betas, update.IsPrerelease);
        Assert.True(UpdateService.IsApprovedUpdate(update));
        Assert.Contains(fixture.Requests, path => path.EndsWith(expected + "/" + update.InstallerName + SignedUpdateManifest.SignatureSuffix));
        Assert.DoesNotContain(fixture.Requests, path => path.Contains("api.github.com"));
        update.Version = "mutated caller value";
        int requests = fixture.Requests.Count;
        Assert.Equal(expected, (await service.CheckForUpdatesAsync(betas))!.Version);
        Assert.Equal(expected, service.LatestUpdateInfo!.Version);
        Assert.Equal(requests, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("signature")]
    [InlineData("version")]
    [InlineData("installer")]
    public async Task FeedHistoryCannotAuthorizeMissingOrInvalidManifestMetadata(string failure)
    {
        using var fixture = new Fixture("99.1.0");
        if (failure != "missing")
        {
            var manifest = new SignedUpdateManifest(1, failure == "version" ? "99.0.0" : "99.1.0",
                failure == "installer" ? UpdateService.SelfContainedSetupName : UpdateService.SetupName, 123, new string('A', 64));
            fixture.Sign("99.1.0", UpdateService.SetupName, manifest, corruptSignature: failure == "signature");
        }
        using var service = fixture.Service();
        Assert.Null(await service.CheckForUpdatesAsync());
        Assert.Null(service.LatestUpdateInfo);
        var listed = Assert.Single(await service.GetAllReleasesAsync());
        Assert.False(listed.IsNewerVersion);
        Assert.Empty(listed.DownloadUrl);
    }

    [Fact]
    public async Task PersistedApiBackoffStillAllowsFreshFeedChecksAndManualRefresh()
    {
        using var fixture = new Fixture("99.1.0");
        fixture.Sign("99.1.0", UpdateService.SetupName);
        string directory = Directory.CreateTempSubdirectory("vnotch-update-fallback-").FullName;
        string cache = Path.Combine(directory, "update-cache.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new
        {
            LastCheckUtc = DateTime.UtcNow.AddDays(-1),
            RetryAfterUtc = DateTime.UtcNow.AddHours(1),
            IncludesPrereleases = false,
            Etag = "\"stale-api-result\"",
            Release = new UpdateInfo
            {
                Version = "2.0.0",
                InstallerName = UpdateService.SetupName,
                DownloadUrl = Asset("2.0.0", UpdateService.SetupName),
                ManifestUrl = Asset("2.0.0", UpdateService.SetupName) + SignedUpdateManifest.ManifestSuffix,
                ManifestSignatureUrl = Asset("2.0.0", UpdateService.SetupName) + SignedUpdateManifest.SignatureSuffix
            }
        }));
        try
        {
            using (var service = fixture.Service(cache))
            {
                Assert.Equal("99.1.0", (await service.CheckForUpdatesAsync())!.Version);
                fixture.Versions = ["99.2.0"];
                fixture.Sign("99.2.0", UpdateService.SetupName);
                Assert.Equal("99.1.0", (await service.CheckForUpdatesAsync())!.Version);
                Assert.Equal("99.2.0", (await service.CheckForUpdatesAsync(forceRefresh: true))!.Version);
            }
            using var restarted = fixture.Service(cache);
            int count = fixture.Requests.Count;
            Assert.Equal("99.2.0", (await restarted.CheckForUpdatesAsync())!.Version);
            Assert.Equal(count, fixture.Requests.Count);
            Assert.DoesNotContain(fixture.Requests, path => path.Contains("api.github.com"));
        }
        finally { SafeFileDeletion.DeleteDirectory(directory); }
    }

    [Fact]
    public async Task FailedFreshCheckNeverReportsAnOldNonNewerCacheAsUpToDate()
    {
        using var fixture = new Fixture("1.0.0");
        using var service = fixture.Service();
        var current = await service.CheckForUpdatesAsync();
        Assert.NotNull(current);
        Assert.False(current.IsNewerVersion);
        fixture.FeedUnavailable = true;
        Assert.Null(await service.CheckForUpdatesAsync(forceRefresh: true));
        Assert.Null(service.LatestUpdateInfo);
        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Fact]
    public async Task ApiTransportFailureAlsoFallsBackToVerifiedPublicAssets()
    {
        using var fixture = new Fixture("99.1.0") { ApiUnavailable = true };
        fixture.Sign("99.1.0", UpdateService.SetupName);
        using var service = fixture.Service();
        Assert.True((await service.CheckForUpdatesAsync())!.IsNewerVersion);
    }

    [Fact]
    public void VerifiedFallbackEnablesTheSettingsDownloadAction() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture("99.1.0");
        fixture.Sign("99.1.0", UpdateService.SetupName);
        using var service = fixture.Service();
        var refs = new SettingsUpdateViewRefs(new Window(), new TextBlock(), new Button(), new Button());
        using var presenter = new SettingsUpdatePresenter(service, refs, () => true);
        await presenter.CheckForUpdatesAsync(forceRefresh: true);
        Assert.Equal(Loc.Get("settings.updateAvailable", "99.1.0"), refs.Status.Text);
        Assert.Equal(Visibility.Visible, refs.Download.Visibility);
        Assert.True(refs.Check.IsEnabled);
    });

    private static string Asset(string version, string installer) =>
        "https://github.com/w0otdev/V-Notch/releases/download/v" + version + "/" + installer;

    private sealed class Fixture(params string[] versions) : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly Dictionary<string, byte[]> _assets = [];
        private HttpClient? _client;
        internal List<string> Requests { get; } = [];
        internal string[] Versions = versions;
        internal bool FeedUnavailable, ApiUnavailable;

        internal UpdateService Service(string? cache = null)
        {
            _clientHandler.Reply = Reply;
            _client ??= new HttpClient(_clientHandler);
            return new UpdateService(_client, new UpdateSecurityPolicy(), manifestPublicKey: _key.ExportSubjectPublicKeyInfoPem(), cachePath: cache);
        }

        private readonly Handler _clientHandler = new();

        internal void Sign(string version, string installer, SignedUpdateManifest? manifest = null, bool corruptSignature = false)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest ?? new(1, version, installer, 123, new string('A', 64)));
            byte[] signature = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (corruptSignature) signature[0] ^= 1;
            _assets[Asset(version, installer) + SignedUpdateManifest.ManifestSuffix] = payload;
            _assets[Asset(version, installer) + SignedUpdateManifest.SignatureSuffix] = signature;
        }

        private HttpResponseMessage Reply(HttpRequestMessage request)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (request.RequestUri.Host == "api.github.com")
            {
                if (ApiUnavailable) throw new HttpRequestException("Fixture API offline");
                return new(HttpStatusCode.Forbidden) { Headers = { RetryAfter = new(TimeSpan.FromHours(1)) } };
            }
            Assert.False(request.Headers.Contains("If-None-Match"));
            if (url.EndsWith("/releases.atom"))
                return FeedUnavailable ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK)
                {
                    Content = new StringContent("<feed xmlns=\"http://www.w3.org/2005/Atom\">" + string.Concat(Versions.Select(version =>
                        $"<entry><id>tag:github.com,2008:Repository/123/v{version}</id><updated>2026-10-31T00:00:00Z</updated><content>Fixture release</content></entry>")) + "</feed>")
                };
            Assert.False(url.EndsWith(".exe"), "Update checks must not download or execute an installer.");
            return _assets.TryGetValue(url, out var bytes) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new(HttpStatusCode.NotFound);
        }

        public void Dispose() { _client?.Dispose(); _key.Dispose(); }

        private sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => throw new InvalidOperationException();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(Reply(request));
        }
    }
}
