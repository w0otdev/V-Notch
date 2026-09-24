using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

public sealed class AppIntegrityServiceTests
{
    [Fact]
    public async Task ComputeFileSha256Async_ReturnsAccurateHash()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"integrity-test-{Guid.NewGuid():N}.txt");
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes("V-Notch Official Integrity Test Payload 2026");
            await File.WriteAllBytesAsync(tempFile, bytes);

            string expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string actual = await AppIntegrityService.ComputeFileSha256Async(tempFile);

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/rainaku/V-Notch/releases/download/v1.9.0/V-Notch-Setup.exe\r\nReferrerUrl=https://github.com/rainaku/V-Notch/releases", "https://github.com/rainaku/V-Notch/releases/download/v1.9.0/V-Notch-Setup.exe", "https://github.com/rainaku/V-Notch/releases")]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=https://sketchy-site.ru/files/vnotch.exe", "https://sketchy-site.ru/files/vnotch.exe", null)]
    [InlineData("[ZoneTransfer]\nZoneId=3\n", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void ParseZoneIdentifier_ExtractsUrlsCorrectly(string? content, string? expectedHost, string? expectedReferrer)
    {
        var (host, referrer) = AppIntegrityService.ParseZoneIdentifier(content!);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedReferrer, referrer);
    }

    [Theory]
    [InlineData("https://github.com/rainaku/V-Notch/releases/download/v1.9.0/V-Notch-Setup.exe", true)]
    [InlineData("https://www.github.com/rainaku/V-Notch/releases", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/12345", true)]
    [InlineData("https://raw.githubusercontent.com/rainaku/V-Notch/main/README.md", true)]
    [InlineData("https://github-production-release-asset-2e65be.s3.amazonaws.com/12345/abc", true)]
    [InlineData("about:internet", true)]
    [InlineData("http://localhost:8080/setup.exe", true)]
    [InlineData("https://drive.google.com/uc?id=12345", false)]
    [InlineData("https://mediafire.com/download/vnotch.exe", false)]
    [InlineData("https://evil-site.com/V-Notch-Setup.exe", false)]
    [InlineData("https://github.com/imposter/malicious-repo/releases/v1.0/V-Notch.exe", false)]
    public void IsTrustedDownloadDomain_ValidatesDomainsCorrectly(string url, bool expectedTrusted)
    {
        bool isTrusted = AppIntegrityService.IsTrustedDownloadDomain(url);
        Assert.Equal(expectedTrusted, isTrusted);
    }

    [Fact]
    public void CheckDownloadOrigin_NonExistentFile_ReturnsTrusted()
    {
        var (isTrusted, untrustedUrl) = AppIntegrityService.CheckDownloadOrigin(@"C:\NonExistent\Path\Fake.exe");
        Assert.True(isTrusted);
        Assert.Null(untrustedUrl);
    }

    [Fact]
    public void CheckDownloadOrigin_FileWithoutZoneIdentifier_ReturnsTrusted()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"no-motw-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(tempFile, "dummy");
            var (isTrusted, untrustedUrl) = AppIntegrityService.CheckDownloadOrigin(tempFile);
            Assert.True(isTrusted);
            Assert.Null(untrustedUrl);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyChecksumAsync_ReturnsVerified_WhenHashMatchesChecksumAsset()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"match-{Guid.NewGuid():N}.exe");
        try
        {
            byte[] fileBytes = Encoding.UTF8.GetBytes("Matching Binary Contents");
            await File.WriteAllBytesAsync(tempFile, fileBytes);
            string fileHash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

            string releaseJson = $$"""
            {
              "tag_name": "v1.9.0",
              "assets": [
                {
                  "name": "checksums.txt",
                  "browser_download_url": "https://test.local/checksums.txt"
                }
              ]
            }
            """;

            string checksumContent = $"{fileHash}  V-Notch.exe\n";

            var handler = new MockHttpMessageHandler((req) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("checksums.txt"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(checksumContent)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson)
                };
            });

            using var client = new HttpClient(handler);
            var status = await AppIntegrityService.VerifyChecksumAsync(tempFile, "1.9.0", client);

            Assert.Equal(IntegrityCheckStatus.Verified, status);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyChecksumAsync_ReturnsMismatch_WhenHashDoesNotMatchChecksumAsset()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"mismatch-{Guid.NewGuid():N}.exe");
        try
        {
            byte[] fileBytes = Encoding.UTF8.GetBytes("Tampered Binary Contents");
            await File.WriteAllBytesAsync(tempFile, fileBytes);

            string releaseJson = """
            {
              "tag_name": "v1.9.0",
              "assets": [
                {
                  "name": "checksums.txt",
                  "browser_download_url": "https://test.local/checksums.txt"
                }
              ]
            }
            """;

            string checksumContent = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  V-Notch.exe\n";

            var handler = new MockHttpMessageHandler((req) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("checksums.txt"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(checksumContent)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson)
                };
            });

            using var client = new HttpClient(handler);
            var status = await AppIntegrityService.VerifyChecksumAsync(tempFile, "1.9.0", client);

            Assert.Equal(IntegrityCheckStatus.HashMismatch, status);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyChecksumAsync_ReturnsReleaseNotFound_On404()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"notfound-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllTextAsync(tempFile, "dummy");

            var handler = new MockHttpMessageHandler((_) =>
                new HttpResponseMessage(HttpStatusCode.NotFound));

            using var client = new HttpClient(handler);
            var status = await AppIntegrityService.VerifyChecksumAsync(tempFile, "99.9.9", client);

            Assert.Equal(IntegrityCheckStatus.ReleaseNotFound, status);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ConfirmationDialog_CanConstructWithDangerStyle()
    {
        SharedStaTestRunner.Run(() =>
        {
            var dialog = new ConfirmationDialog();
            var options = new ConfirmationDialog.DialogOptions(
                Title: "Security Warning",
                ConfirmText: "Confirm",
                CancelText: "Cancel",
                Icon: ConfirmationDialog.DialogIcon.Warning,
                Style: ConfirmationDialog.DialogStyle.Danger);

            dialog.TitleText.Text = options.Title;
            dialog.MessageText.Text = "Testing message";
            dialog.ConfirmButton.Content = options.ConfirmText;
            dialog.CancelButton.Content = options.CancelText;
            dialog.ConfirmButton.Style = (System.Windows.Style)dialog.FindResource("DangerButton");

            Assert.NotNull(dialog);
        });
    }

    [Theory]
    [InlineData("V-Notch.exe.sha256", false, true)]
    [InlineData("V-Notch-SelfContained.exe.sha256", false, true)]
    [InlineData("checksums.txt", false, true)]
    [InlineData("SHA256SUMS.txt", false, true)]
    [InlineData("V-Notch-Setup.exe.sha256", false, false)]
    [InlineData("V-Notch-Setup-SelfContained.exe.sha256", false, false)]
    [InlineData("V-Notch-Setup.exe.manifest.json", false, false)]
    [InlineData("V-Notch-Setup.exe.sha256", true, true)]
    [InlineData("V-Notch-Setup.exe.manifest.json", true, true)]
    [InlineData("V-Notch.exe.sha256", true, false)]
    [InlineData("V-Notch-SelfContained.exe.sha256", true, false)]
    [InlineData("checksums.txt", true, true)]
    public void IsApplicableChecksumAsset_FiltersCorrectly(string assetName, bool isInstaller, bool expected)
    {
        bool actual = AppIntegrityService.IsApplicableChecksumAsset(assetName, isInstaller);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task VerifyChecksumAsync_Skips_WhenOnlySetupChecksumAssetIsPresentForAppBinary()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"V-Notch-{Guid.NewGuid():N}.exe");
        try
        {
            byte[] fileBytes = Encoding.UTF8.GetBytes("Normal App Binary");
            await File.WriteAllBytesAsync(tempFile, fileBytes);

            string releaseJson = """
            {
              "tag_name": "v1.9.3",
              "assets": [
                {
                  "name": "V-Notch-Setup.exe.sha256",
                  "browser_download_url": "https://test.local/V-Notch-Setup.exe.sha256"
                }
              ]
            }
            """;

            string installerChecksumContent = "9ffad98267616332821ec3e4ac36b08f9a03ef0f7687995e489fcd251d208ec8  V-Notch-Setup.exe\n";

            var handler = new MockHttpMessageHandler((req) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("V-Notch-Setup.exe.sha256"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(installerChecksumContent)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson)
                };
            });

            using var client = new HttpClient(handler);
            var status = await AppIntegrityService.VerifyChecksumAsync(tempFile, "1.9.3", client);

            // Must be Skipped, not HashMismatch!
            Assert.Equal(IntegrityCheckStatus.Skipped, status);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
