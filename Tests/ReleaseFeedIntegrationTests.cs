using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed partial class ReleaseFeedIntegrationTests(ITestOutputHelper output)
{
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_update_parse_atom", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ParseReleaseFeed(ReadOnlySpan<byte> data, int size, Span<char> output, int capacity, out int written);

    [Theory]
    [InlineData("<feed><entry><id>unclosed")]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    [InlineData("{{{not-xml-garbage!@#$")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE feed [<!ENTITY xxe \"boom\">]><feed/>")]
    public void ParseAtom_MalformedOrInvalidXml_ReturnsError(string rawInput)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(rawInput);
        char[] outputBuffer = new char[4096];
        int status = ParseReleaseFeed(bytes, bytes.Length, outputBuffer, outputBuffer.Length, out int written);
        Assert.True(status < 0);
        Assert.Equal(0, written);
    }

    [Fact]
    public void ParseAtom_BoundsAndCapacityChecks_ReturnExpectedStatusCodes()
    {
        char[] outputBuffer = new char[64];
        byte[] oversized = new byte[2 * 1024 * 1024 + 1];
        Assert.Equal(-1, ParseReleaseFeed(oversized, oversized.Length, outputBuffer, outputBuffer.Length, out _));
        Assert.Equal(-1, ParseReleaseFeed(oversized, 0, outputBuffer, outputBuffer.Length, out _));

        byte[] validXml = Encoding.UTF8.GetBytes(
            "<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><id>tag:github.com,2008:Repository/1/v2.2.2</id><updated>2026-10-31T00:00:00Z</updated><content type=\"html\">Notes</content></entry></feed>");
        char[] tinyBuffer = new char[4];
        int status = ParseReleaseFeed(validXml, validXml.Length, tinyBuffer, tinyBuffer.Length, out int required);
        Assert.Equal(-2, status);
        Assert.True(required > tinyBuffer.Length);
    }

    [Fact]
    public void ParseAtom_EntriesMissingIdOrUpdatedOrWithEmptyTag_AreSkipped()
    {
        const string atomXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <updated>2026-10-30T10:00:00Z</updated>
                <content type="html">&lt;p&gt;Missing id&lt;/p&gt;</content>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/100/v2.1.0</id>
                <link rel="alternate" type="text/html" href="https://github.com/w0otdev/V-Notch/releases/tag/v2.1.0"/>
                <content type="html">&lt;p&gt;Missing updated timestamp&lt;/p&gt;</content>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/100/</id>
                <updated>2026-10-30T11:00:00Z</updated>
                <content type="html">&lt;p&gt;Empty tag after slash&lt;/p&gt;</content>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/100/v2.2.2</id>
                <updated>2026-10-31T12:00:00Z</updated>
                <content type="html">&lt;h3&gt;Valid entry&lt;/h3&gt;&lt;p&gt;Body text&lt;/p&gt;</content>
              </entry>
            </feed>
            """;

        byte[] bytes = Encoding.UTF8.GetBytes(atomXml);
        char[] buffer = new char[8192];
        int status = ParseReleaseFeed(bytes, bytes.Length, buffer, buffer.Length, out int written);
        Assert.Equal(0, status);

        using var doc = JsonDocument.Parse(new string(buffer, 0, written));
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        var entry = doc.RootElement[0];
        Assert.Equal("v2.2.2", entry.GetProperty("tag_name").GetString());
        Assert.Equal("2026-10-31T12:00:00Z", entry.GetProperty("published_at").GetString());
        Assert.Equal("html", entry.GetProperty("body_format").GetString());
        Assert.Equal("<h3>Valid entry</h3><p>Body text</p>", entry.GetProperty("body").GetString());
    }

    [Fact]
    public async Task MultiEntryAtomFeed_ParsesStableAndPrereleaseTagsReleaseNotesAndMatchesAssets()
    {
        const string atomXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/100/not-a-valid-version</id>
                <updated>2026-10-31T15:00:00Z</updated>
                <content type="html">&lt;p&gt;Invalid version tag&lt;/p&gt;</content>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/100/v99.2.0-beta.14.1</id>
                <updated>2026-10-31T14:00:00Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/w0otdev/V-Notch/releases/tag/v99.2.0-beta.14.1"/>
                <content type="html">&lt;h2&gt;Beta Release&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;Pre-release channel change&lt;/li&gt;&lt;/ul&gt;</content>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/100/v99.1.0</id>
                <updated>2026-10-31T13:00:00Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/w0otdev/V-Notch/releases/tag/v99.1.0"/>
                <content type="html"><![CDATA[
                  <h2>Stable Release</h2>
                  <p>Open <strong>Settings</strong> &amp; choose <code>Translate</code>.</p>
                  <ol><li>Read <a href="https://example.com/notes">release notes</a></li></ol>
                  <ul><li><input type="checkbox" disabled checked>Ready</li><li><input type="checkbox" disabled>Pending</li></ul>
                  <blockquote><p>Keep this setting.</p></blockquote>
                  <pre><code>example()</code></pre>
                  <table><thead><tr><th>Version</th></tr></thead><tbody><tr><td>2.4</td></tr></tbody></table>
                ]]></content>
              </entry>
            </feed>
            """;

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var routes = new Dictionary<string, (byte[] Body, string ContentType)>(StringComparer.Ordinal)
        {
            ["https://github.com/w0otdev/V-Notch/releases.atom"] = (Encoding.UTF8.GetBytes(atomXml), "application/atom+xml")
        };

        AddSignedManifest(routes, ecdsa, "99.1.0", UpdateService.SetupName);
        AddSignedManifest(routes, ecdsa, "99.2.0-beta.14.1", UpdateService.SelfContainedSetupName);

        using var client = new HttpClient(newSyntheticFeedHandler(routes));
        using var updater = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: publicKeyPem);

        var allReleases = await updater.GetAllReleasesAsync();
        Assert.Equal(3, allReleases.Count);
        Assert.Contains(allReleases, r => r.Version == "99.2.0-beta.14.1" && r.IsPrerelease && r.ReleaseNotes.Contains("Pre-release channel change"));
        var stableNotes = Assert.Single(allReleases, r => r.Version == "99.1.0" && !r.IsPrerelease).ReleaseNotes;
        Assert.Contains("Open **Settings** & choose `Translate`.", stableNotes);
        Assert.Contains("[release notes](https://example.com/notes)", stableNotes);
        Assert.Contains("[x] Ready", stableNotes);
        Assert.Contains("[ ] Pending", stableNotes);
        var blocks = VNotch.Controls.MarkdownWpfRenderer.Parse(stableNotes);
        Assert.Collection(blocks,
            block => Assert.Equal(2, Assert.IsType<Markdig.Syntax.HeadingBlock>(block).Level),
            block => Assert.IsType<Markdig.Syntax.ParagraphBlock>(block),
            block => Assert.True(Assert.IsType<Markdig.Syntax.ListBlock>(block).IsOrdered),
            block => Assert.False(Assert.IsType<Markdig.Syntax.ListBlock>(block).IsOrdered),
            block => Assert.IsType<Markdig.Syntax.QuoteBlock>(block),
            block => Assert.IsAssignableFrom<Markdig.Syntax.CodeBlock>(block),
            block => Assert.IsType<Markdig.Extensions.Tables.Table>(block));

        var stableUpdate = await updater.CheckForUpdatesAsync(includePrereleases: false, forceRefresh: true);
        Assert.NotNull(stableUpdate);
        Assert.Equal("99.1.0", stableUpdate!.Version);
        Assert.False(stableUpdate.IsPrerelease);
        Assert.True(stableUpdate.IsNewerVersion);
        Assert.Equal(UpdateService.SetupName, stableUpdate.InstallerName);
        Assert.Equal("https://github.com/w0otdev/V-Notch/releases/download/v99.1.0/V-Notch-Setup.exe", stableUpdate.DownloadUrl);
        Assert.Equal("https://github.com/w0otdev/V-Notch/releases/download/v99.1.0/V-Notch-Setup.exe.manifest.json", stableUpdate.ManifestUrl);
        Assert.Equal("https://github.com/w0otdev/V-Notch/releases/download/v99.1.0/V-Notch-Setup.exe.manifest.sig", stableUpdate.ManifestSignatureUrl);

        var prereleaseUpdate = await updater.CheckForUpdatesAsync(includePrereleases: true, forceRefresh: true);
        Assert.NotNull(prereleaseUpdate);
        Assert.Equal("99.2.0-beta.14.1", prereleaseUpdate!.Version);
        Assert.True(prereleaseUpdate.IsPrerelease);
        Assert.True(prereleaseUpdate.IsNewerVersion);
        Assert.Equal(UpdateService.SelfContainedSetupName, prereleaseUpdate.InstallerName);
        Assert.Equal("https://github.com/w0otdev/V-Notch/releases/download/v99.2.0-beta.14.1/V-Notch-Setup-SelfContained.exe", prereleaseUpdate.DownloadUrl);
    }

    private static void AddSignedManifest(Dictionary<string, (byte[] Body, string ContentType)> routes, ECDsa signer, string version, string installerName)
    {
        string baseUrl = $"https://github.com/w0otdev/V-Notch/releases/download/v{version}/{installerName}";
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateManifest(
            SchemaVersion: 1,
            Version: version,
            InstallerName: installerName,
            Size: 1048576,
            Sha256: new string('a', 64)));
        byte[] signatureBytes = signer.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        routes[baseUrl + SignedUpdateManifest.ManifestSuffix] = (manifestBytes, "application/json");
        routes[baseUrl + SignedUpdateManifest.SignatureSuffix] = (signatureBytes, "application/octet-stream");
    }

    private static HttpMessageHandler newSyntheticFeedHandler(IReadOnlyDictionary<string, (byte[] Body, string ContentType)> routes) =>
        new SyntheticFeedHandler(routes);

    private sealed class SyntheticFeedHandler(IReadOnlyDictionary<string, (byte[] Body, string ContentType)> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != null && routes.TryGetValue(request.RequestUri.AbsoluteUri, out var entry))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(entry.Body)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(entry.ContentType);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DesktopIntegration")]
    public async Task PublicReleaseHistoryLoadsWhenOneEndpointIsUnavailable(bool atomUnavailable)
    {
        var privacy = new NetworkPrivacy();
        privacy.Apply(new() { EnableLocalOnlyMode = false });
        using var client = new HttpClient(privacy.CreateHandler(NetworkFeature.Updates, new SingleEndpointOutage(atomUnavailable)
        {
            InnerHandler = NetworkPrivacy.CreateHttpHandler()
        }))
        { Timeout = TimeSpan.FromSeconds(30) };
        using var updater = new UpdateService(client, new UpdateSecurityPolicy());
        var releases = await updater.GetAllReleasesAsync();
        Assert.NotEmpty(releases);
        Assert.Contains(releases, release => !string.IsNullOrWhiteSpace(release.ReleaseNotes));
        Assert.All(releases, release =>
        {
            Assert.False(string.IsNullOrWhiteSpace(release.ReleaseTag));
            Assert.False(release.IsNewerVersion);
            Assert.Empty(release.DownloadUrl);
        });
        var latest = await updater.CheckForUpdatesAsync(forceRefresh: true);
        Assert.NotNull(latest);
        output.WriteLine($"Loaded {releases.Count} live releases. Installed {updater.CurrentVersion}, checked {latest.Version}.");
    }

    private sealed class SingleEndpointOutage(bool atomUnavailable) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (atomUnavailable && request.RequestUri!.AbsolutePath.EndsWith("/releases.atom", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (!atomUnavailable && request.RequestUri!.Host == "api.github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
