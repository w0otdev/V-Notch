using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SignedUpdateManifestTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("test installer bytes; never executed");
    private static SignedUpdateManifest Manifest => new(1, "99.0.0", UpdateService.SetupName,
        Installer.Length, Convert.ToHexString(SHA256.HashData(Installer)));
    private static byte[] Sign(ECDsa key, byte[] payload) => key.SignData(payload, HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    [Fact]
    public void EmbeddedKey_IsPublicP256()
    {
        string pem = SignedUpdateManifest.ReadPinnedPublicKey();
        Assert.DoesNotContain("PRIVATE", pem);
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void ValidSignature_Accepted_ByteMutationAndDifferentSignerRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(Manifest);
        byte[] signature = Sign(key, payload);
        Assert.Equal(Manifest, SignedUpdateManifest.Verify(payload, signature, key.ExportSubjectPublicKeyInfoPem(),
            "99.0.0", UpdateService.SetupName, "1.9.2"));
        Assert.Throws<InvalidDataException>(() => SignedUpdateManifest.Verify(payload, signature,
            otherKey.ExportSubjectPublicKeyInfoPem(), "99.0.0", UpdateService.SetupName, "1.9.2"));
        payload[^2] ^= 1;
        Assert.Throws<InvalidDataException>(() => SignedUpdateManifest.Verify(payload, signature,
            key.ExportSubjectPublicKeyInfoPem(), "99.0.0", UpdateService.SetupName, "1.9.2"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("zero-size")]
    [InlineData("huge-size")]
    [InlineData("hash")]
    [InlineData("downgrade")]
    [InlineData("same-version")]
    public void SignedButInvalidMetadata_IsRejected(string scenario)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = scenario switch
        {
            "schema" => Manifest with { SchemaVersion = 2 },
            "name" => Manifest with { InstallerName = UpdateService.SelfContainedSetupName },
            "version" => Manifest with { Version = "98.0.0" },
            "zero-size" => Manifest with { Size = 0 },
            "huge-size" => Manifest with { Size = UpdateSecurityPolicy.MaximumInstallerBytes + 1 },
            "hash" => Manifest with { Sha256 = new string('Z', 64) },
            _ => Manifest
        };
        string current = scenario switch { "downgrade" => "100.0.0", "same-version" => "99.0.0", _ => "1.9.2" };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        Assert.Throws<InvalidDataException>(() => SignedUpdateManifest.Verify(payload, Sign(key, payload),
            key.ExportSubjectPublicKeyInfoPem(), "99.0.0", UpdateService.SetupName, current));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("tampered-installer")]
    [InlineData("truncated-installer")]
    [InlineData("bad-signature")]
    [InlineData("missing-signature")]
    [InlineData("oversized-manifest")]
    [InlineData("authenticode-denied")]
    public async Task DownloadVerification_EnforcesTrustBeforeExecution(string scenario)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(Manifest);
        byte[] signature = Sign(key, payload);
        byte[] installer = Installer.ToArray();
        if (scenario == "tampered-installer") installer[0] ^= 1;
        if (scenario == "truncated-installer") installer = installer[..^1];
        if (scenario == "bad-signature") signature[0] ^= 1;
        if (scenario == "oversized-manifest") payload = new byte[SignedUpdateManifest.MaximumManifestBytes + 1];
        bool installerRequested = false;
        using var client = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/installer") installerRequested = true;
            if (path == "/signature" && scenario == "missing-signature")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            byte[] bytes = path switch { "/manifest" => payload, "/signature" => signature, _ => installer };
            // No Content-Length: limits must be enforced while streaming, too.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(bytes)) };
        }));
        var service = new UpdateService(client, new UpdateSecurityPolicy(),
            _ => (scenario != "authenticode-denied", "Additional policy denied"), key.ExportSubjectPublicKeyInfoPem());
        string destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            if (scenario == "valid")
            {
                await service.DownloadAndVerifyInstallerAsync(Update(), destination, null, default);
                Assert.Equal(Installer, await File.ReadAllBytesAsync(destination));
            }
            else
            {
                var exception = await Record.ExceptionAsync(() => service.DownloadAndVerifyInstallerAsync(Update(), destination, null, default));
                Assert.NotNull(exception);
                if (scenario is "bad-signature" or "missing-signature" or "oversized-manifest")
                    Assert.False(installerRequested);
            }
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    [Fact]
    public async Task LegacyRelease_IsNotOffered_NewReleasePreservesManifestUrlsInCache()
    {
        foreach (bool signed in new[] { false, true })
        {
            var names = new List<string> { UpdateService.SetupName, UpdateService.SetupName + ".sha256" };
            if (signed) names.AddRange([UpdateService.SetupName + SignedUpdateManifest.ManifestSuffix,
                UpdateService.SetupName + SignedUpdateManifest.SignatureSuffix]);
            string json = JsonSerializer.Serialize(new
            {
                tag_name = "v99.0.0",
                body = "notes",
                published_at = DateTime.UtcNow,
                assets = names.Select(name => new { name, browser_download_url = "https://example.test/" + name })
            });
            using var client = new HttpClient(new Handler(request => new(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/latest") ? json : "[" + json + "]")
            }));
            var service = new UpdateService(client, new UpdateSecurityPolicy());
            var first = await service.CheckForUpdatesAsync();
            var cached = await service.CheckForUpdatesAsync();
            Assert.NotNull(first);
            Assert.NotNull(cached);
            Assert.Equal(signed, first.IsNewerVersion);
            Assert.Equal(first.ManifestUrl, cached.ManifestUrl);
            Assert.Equal(first.ManifestSignatureUrl, cached.ManifestSignatureUrl);
            var listed = Assert.Single(await service.GetAllReleasesAsync());
            Assert.Equal(signed, listed.IsNewerVersion);
            Assert.Equal(first.ManifestUrl, listed.ManifestUrl);
        }
    }

    [Fact]
    public void Approval_RejectsLegacyOrInsecureUrls()
    {
        var update = Update();
        Assert.True(UpdateService.IsApprovedUpdate(update));
        update.ManifestUrl = "http://example.test/manifest";
        Assert.False(UpdateService.IsApprovedUpdate(update));
        update.ManifestUrl = "";
        Assert.False(UpdateService.IsApprovedUpdate(update));
    }

    [Fact]
    public async Task ReleaseSigningScript_ProducesManifestAcceptedByUpdater_AndLegacyChecksum()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "V-Notch.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        string directory = Path.Combine(Path.GetTempPath(), "vnotch-signing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string privatePath = Path.Combine(directory, "test-private.pem");
        string publicPath = Path.Combine(directory, "test-public.pem");
        string installerPath = Path.Combine(directory, UpdateService.SetupName);
        try
        {
            await File.WriteAllTextAsync(privatePath, key.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(publicPath, key.ExportSubjectPublicKeyInfoPem());
            await File.WriteAllBytesAsync(installerPath, Installer);
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(root.FullName, "scripts", "Sign-UpdateManifest.ps1"),
                "-InstallerPath", installerPath, "-Version", "99.0.0", "-PrivateKeyPath", privatePath, "-PublicKeyPath", publicPath })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            await Task.WhenAll(output, error);
            Assert.True(process.ExitCode == 0, "Signing script failed: " + await error);
            var verified = SignedUpdateManifest.Verify(
                await File.ReadAllBytesAsync(installerPath + SignedUpdateManifest.ManifestSuffix),
                await File.ReadAllBytesAsync(installerPath + SignedUpdateManifest.SignatureSuffix),
                key.ExportSubjectPublicKeyInfoPem(), "99.0.0", UpdateService.SetupName, "1.9.2");
            Assert.Equal(Manifest.Size, verified.Size);
            string sidecar = await File.ReadAllTextAsync(installerPath + ".sha256");
            Assert.Equal(Manifest.Sha256.ToLowerInvariant() + "  " + UpdateService.SetupName, sidecar);
        }
        finally
        {
            // Only files created by this test in its unique temporary directory.
            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static UpdateInfo Update() => new()
    {
        Version = "99.0.0",
        InstallerName = UpdateService.SetupName,
        DownloadUrl = "https://example.test/installer",
        ManifestUrl = "https://example.test/manifest",
        ManifestSignatureUrl = "https://example.test/signature"
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
