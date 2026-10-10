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
    [InlineData("99.1.0-beta.10", "99.0.0", "99.1.0-beta.10", true)]
    [InlineData("99.0.0-beta.10", "99.0.0", "99.0.0", false)]
    [InlineData("99.0.0-beta.10", "99.1.0", "99.1.0", false)]
    [InlineData("99.1.0", "99.0.0", "99.1.0", true)] // GitHub can flag numeric tags as prerelease.
    [InlineData("99.0.0-beta.65534.65534", "99.0.0", "99.0.0", false)]
    public async Task BetaChannelSelectsHighestApprovedVersionRegardlessOfPublicationOrder(
        string beta, string stable, string expected, bool isPrerelease)
    {
        using var client = new HttpClient(new Handler(_ => Json(new[]
        {
            Release("100.0.0-beta.1", prerelease: true, draft: true),
            Release(stable), Release(beta, prerelease: true),
            Release("999.0.0", signed: false), Release("invalid", prerelease: true)
        })));
        using var service = new UpdateService(client, new UpdateSecurityPolicy());
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
    public async Task SwitchingChannelsClearsCacheAndEtagAndNeverReturnsBetaOnStableFailure()
    {
        var paths = new List<string>();
        bool failStable = false;
        using var client = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.False(request.Headers.Contains("If-None-Match"));
            if (request.RequestUri.AbsolutePath.EndsWith("/latest"))
                return failStable ? new(HttpStatusCode.ServiceUnavailable) : Json(Release("99.0.0"));
            var response = Json(new[] { Release("99.1.0-beta.1", prerelease: true) });
            response.Headers.ETag = new("\"beta-etag\"");
            return response;
        }));
        using var service = new UpdateService(client, new UpdateSecurityPolicy());
        Assert.Equal("99.0.0", (await service.CheckForUpdatesAsync())!.Version);
        Assert.Equal("99.1.0-beta.1", (await service.CheckForUpdatesAsync(true))!.Version);
        Assert.Equal("99.1.0-beta.1", (await service.CheckForUpdatesAsync(true))!.Version);
        Assert.Equal(2, paths.Count);
        failStable = true;
        Assert.Null(await service.CheckForUpdatesAsync(false));
        Assert.Null(service.LatestUpdateInfo);
        failStable = false;
        Assert.Equal("99.0.0", (await service.CheckForUpdatesAsync(false))!.Version);
        Assert.Equal(4, paths.Count);
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

    [Theory]
    [InlineData(true, "99.1.0")]
    [InlineData(false, "99.1.0-beta.1")]
    public async Task StableChannelRejectsPrereleasesByFlagOrSuffix(bool flag, string version)
    {
        using var client = new HttpClient(new Handler(_ => Json(Release(version, prerelease: flag))));
        using var service = new UpdateService(client, new UpdateSecurityPolicy());
        Assert.Null(await service.CheckForUpdatesAsync());
    }

    [Theory]
    [InlineData("100.0.0-beta.1", true)]
    [InlineData("99.0.0", false)]
    public async Task BetaChannelFollowsPaginationToFindHigherVersion(string latestVersion, bool prerelease)
    {
        int requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            return request.RequestUri!.Query.Contains("page=2")
                ? Json(new[] { Release(latestVersion, prerelease) })
                : Json(Enumerable.Range(0, 100).Select(_ => Release("99.0.0-beta.65534.65534", prerelease: true)));
        }));
        using var service = new UpdateService(client, new UpdateSecurityPolicy());
        var update = await service.CheckForUpdatesAsync(true);
        Assert.Equal(latestVersion, update!.Version);
        Assert.Equal(prerelease, update.IsPrerelease);
        Assert.Equal(2, requests);
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

    private static object Release(string version, bool prerelease = false, bool draft = false, bool signed = true) => new
    {
        tag_name = "v" + version,
        prerelease,
        draft,
        body = "notes",
        published_at = DateTime.UtcNow,
        assets = (signed ? new[] { UpdateService.SetupName, UpdateService.SetupName + SignedUpdateManifest.ManifestSuffix,
            UpdateService.SetupName + SignedUpdateManifest.SignatureSuffix } : new[] { UpdateService.SetupName })
            .Select(name => new { name, browser_download_url = "https://github.com/rainaku/V-Notch/releases/download/v" + version + "/" + name })
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
}
