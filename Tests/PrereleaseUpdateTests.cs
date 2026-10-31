using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrereleaseUpdateTests
{
    [Theory]
    [InlineData("<feed><entry>")]
    [InlineData("<!DOCTYPE feed [<!ENTITY example SYSTEM 'https://example.invalid/entity'>]><feed>&example;</feed>")]
    public async Task InvalidOrExternalEntityFeedCannotSupplyReleaseHistory(string feed)
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(feed) }));
        using var service = new UpdateService(client, new UpdateSecurityPolicy());
        Assert.Empty(await service.GetAllReleasesAsync());
    }

    [Fact]
    public async Task ReleaseHistoryFallsBackToPublicFeedWhenApiIsRateLimited()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal("/w0otdev/V-Notch/releases.atom", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                <feed xmlns="http://www.w3.org/2005/Atom"><entry>
                <id>tag:github.com,2008:Repository/123/v2.2.0-beta.298.1</id>
                <updated>2026-10-29T12:26:49Z</updated>
                <content type="html">&lt;p&gt;Recent &amp;amp; safe release&lt;/p&gt;</content>
                </entry><entry><id>tag:github.com,2008:Repository/123/v2.1.0</id>
                <updated>2026-09-01T00:00:00Z</updated><content type="html">Stable</content></entry></feed>
                """) };
        }));
        using var service = new UpdateService(client, new UpdateSecurityPolicy());
        var releases = await service.GetAllReleasesAsync();
        Assert.Equal(2, releases.Count);
        Assert.Equal("2.2.0-beta.298.1", releases[0].Version);
        Assert.True(releases[0].IsPrerelease);
        Assert.Contains("Recent", releases[0].ReleaseNotes);
        Assert.False(releases[0].IsNewerVersion); // History cannot authorize an installer.
        Assert.Empty(releases[0].DownloadUrl);
        Assert.Equal(2, (await service.GetAllReleasesAsync()).Count);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("99.1.0-beta.10", "99.0.0", "99.1.0-beta.10", true)]
    [InlineData("99.0.0-beta.10", "99.0.0", "99.0.0", false)]
    [InlineData("99.0.0-beta.10", "99.1.0", "99.1.0", false)]
    [InlineData("99.0.0-beta.65534.65534", "99.0.0", "99.0.0", false)]
    public async Task BetaChannelSelectsHighestApprovedVersionRegardlessOfPublicationOrder(
        string beta, string stable, string expected, bool isPrerelease)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stable, beta };
        using var client = new HttpClient(new Handler(request => ReplyFeedOrManifest(
            request, key, [stable, beta, "999.0.0", "invalid"], signed)));
        using var service = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: key.ExportSubjectPublicKeyInfoPem());
        var update = await service.CheckForUpdatesAsync(true);
        Assert.NotNull(update);
        Assert.Equal(expected, update.Version);
        Assert.Equal(isPrerelease, update.IsPrerelease);
        Assert.True(update.IsNewerVersion);
        var cached = await service.CheckForUpdatesAsync(true);
        Assert.Equal(isPrerelease, cached!.IsPrerelease);
        Assert.Equal(isPrerelease, service.LatestUpdateInfo!.IsPrerelease);
    }

    [Fact]
    public async Task ManualForceRefreshBypassesMinRefreshIntervalAndUpdatesToNewestBeta()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99.1.0-beta.288.1", "99.1.0-beta.298.1" };
        int feedRequests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases.atom"))
            {
                feedRequests++;
                if (feedRequests > 1 && request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"beta-288\""))
                {
                    var updated = Feed(["99.1.0-beta.298.1", "99.1.0-beta.288.1"]);
                    updated.Headers.ETag = new("\"beta-298\"");
                    return updated;
                }
                var initial = Feed(["99.1.0-beta.288.1"]);
                initial.Headers.ETag = new("\"beta-288\"");
                return initial;
            }
            return ReplyFeedOrManifest(request, key, [], signed);
        }));
        using var service = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: key.ExportSubjectPublicKeyInfoPem());
        Assert.Equal("99.1.0-beta.288.1", (await service.CheckForUpdatesAsync(includePrereleases: true))!.Version);
        Assert.Equal("99.1.0-beta.288.1", (await service.CheckForUpdatesAsync(includePrereleases: true, forceRefresh: false))!.Version);
        Assert.Equal(1, feedRequests);
        var forced = await service.CheckForUpdatesAsync(includePrereleases: true, forceRefresh: true);
        Assert.Equal("99.1.0-beta.298.1", forced!.Version);
        Assert.True(forced.IsNewerVersion);
        Assert.Equal(2, feedRequests);
    }

    [Fact]
    public async Task SwitchingChannelsClearsCacheAndEtagAndNeverReturnsBetaOnStableFailure()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99.0.0", "99.1.0-beta.1" };
        var feedPaths = new List<string>();
        bool failFeed = false;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases.atom"))
            {
                feedPaths.Add(request.RequestUri.PathAndQuery);
                Assert.False(request.Headers.Contains("If-None-Match"));
                if (failFeed) return new(HttpStatusCode.ServiceUnavailable);
                var response = Feed(["99.1.0-beta.1", "99.0.0"]);
                response.Headers.ETag = new("\"feed-etag\"");
                return response;
            }
            return ReplyFeedOrManifest(request, key, [], signed);
        }));
        using var service = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: key.ExportSubjectPublicKeyInfoPem());
        Assert.Equal("99.0.0", (await service.CheckForUpdatesAsync())!.Version);
        Assert.Equal("99.1.0-beta.1", (await service.CheckForUpdatesAsync(true))!.Version);
        Assert.Equal("99.1.0-beta.1", (await service.CheckForUpdatesAsync(true))!.Version);
        Assert.Equal(2, feedPaths.Count);
        failFeed = true;
        Assert.Null(await service.CheckForUpdatesAsync(false));
        Assert.Null(service.LatestUpdateInfo);
        failFeed = false;
        Assert.Equal("99.0.0", (await service.CheckForUpdatesAsync(false))!.Version);
        Assert.Equal(4, feedPaths.Count);
        Assert.All(feedPaths, path => Assert.Equal("/w0otdev/V-Notch/releases.atom", path));
    }

    [Theory]
    [InlineData("2.0.1", "2.0.1-beta.65534.65534", 1)]
    [InlineData("2.0.1-beta.65534.65534", "2.0.1", -1)]
    [InlineData("2.0.2", "2.0.1-beta.184.1", 1)]
    [InlineData("2.0.0", "2.0.1-beta.184.1", -1)]
    [InlineData("2.0.1.0", "2.0.1-beta.184.1", 1)]
    [InlineData("2.0.1+build.200", "2.0.1-beta.184.1+build.999", 1)]
    [InlineData("2.0.1-beta.184.2", "2.0.1-beta.184.1", 1)]
    [InlineData("2.0.1-beta.185.1", "2.0.1-beta.184.99", 1)]
    [InlineData("2.0.1+build.200", "2.0.1+build.999", 0)]
    public void ReleaseOrderAllowsStablePromotionAndNeverDowngrades(string target, string current, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(UpdateService.CompareVersions(target, current)));

    [Fact]
    public async Task StableChannelRejectsPrereleasesBySuffix()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99.1.0-beta.1" };
        using var client = new HttpClient(new Handler(request => ReplyFeedOrManifest(request, key, ["99.1.0-beta.1"], signed)));
        using var service = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: key.ExportSubjectPublicKeyInfoPem());
        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Theory]
    [InlineData("100.0.0-beta.1", true)]
    [InlineData("99.0.0", false)]
    public async Task BetaChannelFindsHigherVersionAcrossLargeFeed(string latestVersion, bool prerelease)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99.0.0-beta.65534.65534", latestVersion };
        var versions = Enumerable.Repeat("99.0.0-beta.65534.65534", 100).Append(latestVersion).ToArray();
        using var client = new HttpClient(new Handler(request => ReplyFeedOrManifest(request, key, versions, signed)));
        using var service = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: key.ExportSubjectPublicKeyInfoPem());
        var update = await service.CheckForUpdatesAsync(true);
        Assert.Equal(latestVersion, update!.Version);
        Assert.Equal(prerelease, update.IsPrerelease);
    }

    [Theory]
    [InlineData("99.0.0-beta.2", "99.0.0-beta.2", "99.0.0-beta.1", true)]
    [InlineData("99.0.0", "99.0.0", "99.0.0-beta.2", true)]
    [InlineData("2.0.1", "2.0.1", "2.0.1-beta.65534.65534+build.999", true)]
    [InlineData("2.0.2", "2.0.2", "2.0.1-beta.184.1", true)]
    [InlineData("2.0.0", "2.0.0", "2.0.1-beta.184.1", false)]
    [InlineData("99.0.0-beta.2", "99.0.0-beta.3", "98.0.0", false)]
    [InlineData("99.0.0-beta.2", "99.0.0-beta.2", "99.0.0", false)]
    [InlineData("99.0.0-beta.2", "99.0.0-beta.2", "99.0.0-beta.2", false)]
    [InlineData("invalid", "invalid", "98.0.0", false)]
    public void SignedBetaManifestsRequireMatchingNewerVersion(string version, string expected, string current, bool valid)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new SignedUpdateManifest(1, version, UpdateService.SetupName, 100, new string('A', 64));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (valid)
            Assert.Equal(version, SignedUpdateManifest.Verify(payload, signature, key.ExportSubjectPublicKeyInfoPem(), expected, UpdateService.SetupName, current).Version);
        else
            Assert.Throws<InvalidDataException>(() => SignedUpdateManifest.Verify(payload, signature, key.ExportSubjectPublicKeyInfoPem(), expected, UpdateService.SetupName, current));
    }

    [Fact]
    public void PrereleaseOptInDefaultsOffAndSurvivesSerializationAndCloning()
    {
        Assert.False(JsonSerializer.Deserialize<NotchSettings>("{}")!.IncludePrereleaseUpdates);
        var settings = new NotchSettings { IncludePrereleaseUpdates = true };
        Assert.True(settings.Clone().IncludePrereleaseUpdates);
        Assert.False(settings.ValueEquals(new NotchSettings()));
        Assert.True(JsonSerializer.Deserialize<NotchSettings>(JsonSerializer.Serialize(settings))!.IncludePrereleaseUpdates);
    }

    private static HttpResponseMessage Feed(IEnumerable<string> versions) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<feed xmlns=\"http://www.w3.org/2005/Atom\">" + string.Concat(versions.Select(version =>
            $"<entry><id>tag:github.com,2008:Repository/123/v{version}</id><updated>2026-10-31T00:00:00Z</updated><content>notes</content></entry>")) + "</feed>")
    };

    private static HttpResponseMessage ReplyFeedOrManifest(
        HttpRequestMessage request, ECDsa key, IEnumerable<string> versions, IReadOnlySet<string> signedVersions)
    {
        string url = request.RequestUri!.AbsoluteUri;
        if (url.EndsWith("/releases.atom")) return Feed(versions);
        foreach (string version in signedVersions)
        {
            string baseUrl = "https://github.com/w0otdev/V-Notch/releases/download/" + Uri.EscapeDataString("v" + version) + "/" + UpdateService.SetupName;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateManifest(1, version, UpdateService.SetupName, 123, new string('A', 64)));
            if (url == baseUrl + SignedUpdateManifest.ManifestSuffix)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            if (url == baseUrl + SignedUpdateManifest.SignatureSuffix)
            {
                byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(signature) };
            }
        }
        return new(HttpStatusCode.NotFound);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
}
