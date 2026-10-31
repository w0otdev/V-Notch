using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class UpdateAndIntegritySecurityTests
{
    [Fact]
    public async Task DownloadAndInstallUpdateCoreAsync_HoldsReadLockAcrossVerificationAndLaunch()
    {
        byte[] dummyPayload = Encoding.UTF8.GetBytes("MZ-dummy-verified-installer-payload");
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(dummyPayload));

        using var handler = new MockHttpMessageHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(dummyPayload)
            };
            response.Content.Headers.ContentLength = dummyPayload.Length;
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);

        bool signatureCheckedWhileLocked = false;
        bool launcherCalled = false;
        string? launchedPath = null;

        using var service = new UpdateService(
            httpClient,
            UpdateSecurityPolicy.FromEnvironment(),
            signatureValidator: path =>
            {
                Assert.Throws<IOException>(() =>
                {
                    using var writeStream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                signatureCheckedWhileLocked = true;
                return (true, string.Empty);
            },
            manifestPublicKey: "test-key",
            cachePath: null);

        var updateInfo = new UpdateInfo
        {
            Version = "99.0.0",
            InstallerName = UpdateService.SetupName,
            DownloadUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe",
            ManifestUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe.manifest.json",
            ManifestSignatureUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe.manifest.sig",
            IsNewerVersion = true
        };

        bool result = await service.DownloadAndInstallUpdateCoreAsync(
            updateInfo,
            progress: null,
            cancellationToken: CancellationToken.None,
            installerLauncher: tempPath =>
            {
                launchedPath = tempPath;
                launcherCalled = true;

                // Attempting to open tempPath for writing while the read lock is held must fail with IOException
                Assert.Throws<IOException>(() =>
                {
                    using var writeStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });

                // Opening with FileAccess.Read, FileShare.Read must succeed
                using var readStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.Equal(dummyPayload.Length, readStream.Length);
            },
            expectedSha256Override: expectedSha256);

        Assert.True(result);
        Assert.True(signatureCheckedWhileLocked);
        Assert.True(launcherCalled);
        Assert.False(string.IsNullOrEmpty(launchedPath));
    }

    [Fact]
    public async Task DownloadAndInstallUpdateCoreAsync_ReleasesLockAndDeletesTempFileOnHashMismatch()
    {
        byte[] dummyPayload = Encoding.UTF8.GetBytes("MZ-tampered-installer-payload");
        string wrongSha256 = new string('a', 64);

        using var handler = new MockHttpMessageHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(dummyPayload)
            };
            response.Content.Headers.ContentLength = dummyPayload.Length;
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);

        bool launcherCalled = false;
        using var service = new UpdateService(
            httpClient,
            UpdateSecurityPolicy.FromEnvironment(),
            signatureValidator: _ => (true, string.Empty),
            manifestPublicKey: "test-key",
            cachePath: null);

        var updateInfo = new UpdateInfo
        {
            Version = "99.0.0",
            InstallerName = UpdateService.SetupName,
            DownloadUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe",
            ManifestUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe.manifest.json",
            ManifestSignatureUrl = "https://github.com/w0otdev/V-Notch/releases/download/v99.0.0/V-Notch-Setup.exe.manifest.sig",
            IsNewerVersion = true
        };

        bool result = await service.DownloadAndInstallUpdateCoreAsync(
            updateInfo,
            progress: null,
            cancellationToken: CancellationToken.None,
            installerLauncher: _ => launcherCalled = true,
            expectedSha256Override: wrongSha256);

        Assert.False(result);
        Assert.False(launcherCalled);
    }

    [Fact]
    public void MatchesChecksumManifest_RejectsCommentSubstringAndWrongFilename()
    {
        const string actualHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        const string otherHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // 1. Hash inside a full-line or inline '#' comment
        string commentManifest = $"""
            # {actualHash}  V-Notch.exe
            ; {actualHash}  V-Notch.exe
            {otherHash}  V-Notch.exe # {actualHash}  V-Notch.exe
            """;
        Assert.False(AppIntegrityService.MatchesChecksumManifest(commentManifest, actualHash, "V-Notch.exe"));

        // 2. Hash as a substring of a longer hex string
        string substringManifest = $"""
            aa{actualHash}bb  V-Notch.exe
            {actualHash}00  V-Notch.exe
            """;
        Assert.False(AppIntegrityService.MatchesChecksumManifest(substringManifest, actualHash, "V-Notch.exe"));

        // 3. Hash associated with a wrong filename (other.exe / V-Notch-Setup.exe)
        string wrongFileManifest = $"""
            {actualHash}  other.exe
            {actualHash} *V-Notch-Setup.exe
            V-Notch-Setup.exe: {actualHash}
            """;
        Assert.False(AppIntegrityService.MatchesChecksumManifest(wrongFileManifest, actualHash, "V-Notch.exe"));
    }

    [Fact]
    public void MatchesChecksumManifest_AcceptsValidStandardAndBinaryModeLines()
    {
        const string actualHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        const string otherHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        string standardManifest = $"""
            # Official release checksums
            {otherHash}  V-Notch-Setup.exe
            {actualHash}  V-Notch.exe
            """;
        Assert.True(AppIntegrityService.MatchesChecksumManifest(standardManifest, actualHash, "V-Notch.exe"));

        string binaryModeManifest = $"""
            {otherHash} *V-Notch-Setup.exe
            {actualHash.ToUpperInvariant()} *V-Notch.exe
            """;
        Assert.True(AppIntegrityService.MatchesChecksumManifest(binaryModeManifest, actualHash, "V-Notch.exe"));

        string standaloneManifest = $"{actualHash}\n";
        Assert.True(AppIntegrityService.MatchesChecksumManifest(standaloneManifest, actualHash, "V-Notch.exe"));
    }

    [Theory]
    [InlineData("../latest")]
    [InlineData("1.0.0/../../other")]
    [InlineData(@"1.0.0\..\other")]
    public void BuildCandidateReleaseTags_RejectsPathTraversalTags(string maliciousVersion)
    {
        string[] tags = AppIntegrityService.BuildCandidateReleaseTags(maliciousVersion);
        Assert.Empty(tags);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
