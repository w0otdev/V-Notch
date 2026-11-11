using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using VNotch.Services;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
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
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/w0otdev/V-Notch/releases/download/v1.9.0/V-Notch-Setup.exe\r\nReferrerUrl=https://github.com/w0otdev/V-Notch/releases", "https://github.com/w0otdev/V-Notch/releases/download/v1.9.0/V-Notch-Setup.exe", "https://github.com/w0otdev/V-Notch/releases")]
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
    [InlineData("https://github.com/w0otdev/V-Notch/releases/download/v1.9.0/V-Notch-Setup.exe", true)]
    [InlineData("https://www.github.com/w0otdev/V-Notch/releases", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/12345", true)]
    [InlineData("https://raw.githubusercontent.com/w0otdev/V-Notch/main/README.md", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/12345", true)]
    [InlineData("https://github-production-release-asset-2e65be.s3.amazonaws.com/12345/abc", false)]
    [InlineData("about:internet", true)]
    [InlineData("http://localhost:8080/setup.exe", false)]
    [InlineData("https://attackergithubusercontent.com/setup.exe", false)]
    [InlineData("https://evil.githubusercontent.com/setup.exe", false)]
    [InlineData("https://github-production-release-asset-hacker.s3.amazonaws.com/setup.exe", false)]
    [InlineData("https://github.com/w0otdev/V-Notch-evil/releases", false)]
    [InlineData("https://github.com/w0otdev/other-repo/releases", false)]
    [InlineData("https://raw.githubusercontent.com/imposter/repo/main/setup.exe", false)]
    [InlineData("https://example.test/setup.exe", false)]
    [InlineData("https://github.com:8443/w0otdev/V-Notch/releases", false)]
    [InlineData("https://user@github.com/w0otdev/V-Notch/releases", false)]
    [InlineData("http://github.com/w0otdev/V-Notch/releases", false)]
    [InlineData("not a URL", false)]
    [InlineData("https://drive.google.com/uc?id=12345", false)]
    [InlineData("https://mediafire.com/download/vnotch.exe", false)]
    [InlineData("https://evil-site.com/V-Notch-Setup.exe", false)]
    [InlineData("https://github.com/imposter/malicious-repo/releases/v1.0/V-Notch.exe", false)]
    public void IsTrustedDownloadDomain_ValidatesDomainsCorrectly(string url, bool expectedTrusted)
    {
        bool isTrusted = AppIntegrityService.IsTrustedDownloadDomain(url);
        Assert.Equal(expectedTrusted, isTrusted);
    }

    [Theory]
    [InlineData("https://github.com/w0otdev/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe", true)]
    [InlineData("https://github.com/rainaku/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe", true)]
    [InlineData("http://github.com/w0otdev/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe", false)]
    [InlineData("https://github.com:8443/w0otdev/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe", false)]
    [InlineData("https://user@github.com/w0otdev/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe", false)]
    [InlineData("https://github.com/other/repo/releases/download/v2.0.0/V-Notch-Setup.exe", false)]
    public void IsOfficialReleaseUrl_ValidatesReleaseAssetUrls(string url, bool expected)
    {
        Assert.Equal(expected, AppIntegrityService.IsOfficialReleaseUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("objects.githubusercontent.com", true)]
    [InlineData("release-assets.githubusercontent.com", true)]
    [InlineData("evil.githubusercontent.com", false)]
    [InlineData("github.com", false)]
    [InlineData("", false)]
    public void IsReleaseAssetHost_ValidatesTrustedHosts(string host, bool expected)
    {
        Assert.Equal(expected, AppIntegrityService.IsReleaseAssetHost(host));
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123", true)]
    [InlineData("https://github.com/w0otdev/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe", true)]
    [InlineData("http://objects.githubusercontent.com/github-production-release-asset/123", false)]
    [InlineData("https://user@objects.githubusercontent.com/github-production-release-asset/123", false)]
    [InlineData("https://evil.example/V-Notch-Setup.exe", false)]
    public void IsTrustedReleaseRedirect_ValidatesRedirects(string url, bool expected)
    {
        Assert.Equal(expected, AppIntegrityService.IsTrustedReleaseRedirect(new Uri(url)));
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
}
