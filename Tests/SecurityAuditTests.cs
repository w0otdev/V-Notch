using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight.Providers;
using Xunit;

namespace VNotch.Tests;

public sealed class SecurityAuditTests
{
    [Fact]
    public void SensitiveDataScrubber_RedactsSpotifyCookie()
    {
        string input = "User logged in with cookie: sp_dc=AQB_secret_token_12345ABCDE in session";
        string scrubbed = SensitiveDataScrubber.Scrub(input);

        Assert.DoesNotContain("AQB_secret_token_12345ABCDE", scrubbed);
        Assert.Contains("sp_dc=[REDACTED]", scrubbed);
    }

    [Fact]
    public void SensitiveDataScrubber_RedactsGoogleApiKey() //example key lol
    {
        string input = "Calling YouTube API with key=AIzaSyD_abc1234567890XYZ_abcdef12345678";
        string scrubbed = SensitiveDataScrubber.Scrub(input);

        Assert.DoesNotContain("AIzaSyD_abc1234567890XYZ_abcdef12345678", scrubbed);
        Assert.Contains("AIza[REDACTED]", scrubbed);
    }

    [Fact]
    public void SensitiveDataScrubber_RedactsDpapiCiphertext()
    {
        string input = "Saved encrypted value enc:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA12345678 to disk";
        string scrubbed = SensitiveDataScrubber.Scrub(input);

        Assert.DoesNotContain("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA12345678", scrubbed);
        Assert.Contains("enc:[REDACTED]", scrubbed);
    }

    [Fact]
    public void SensitiveDataScrubber_RedactsBearerToken()
    {
        string input = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.t-ID";
        string scrubbed = SensitiveDataScrubber.Scrub(input);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", scrubbed);
        Assert.Contains("Bearer [REDACTED]", scrubbed);
    }

    [Fact]
    public void SensitiveDataScrubber_RedactsSensitiveUrlQueryParameters()
    {
        string input = "https://api.example.com/v1?api_key=secret_value_123&client_secret=secret_xyz";
        string scrubbed = SensitiveDataScrubber.Scrub(input);

        Assert.DoesNotContain("secret_value_123", scrubbed);
        Assert.DoesNotContain("secret_xyz", scrubbed);
        Assert.Contains("api_key=[REDACTED]", scrubbed);
        Assert.Contains("client_secret=[REDACTED]", scrubbed);
    }

    [Theory]
    [InlineData("https://github.com/rainaku/V-Notch")]
    [InlineData("http://localhost:5000/api")]
    [InlineData("mailto:support@vnotch.com")]
    [InlineData("ms-settings:batterysaver")]
    public void SafeLauncher_PermitsSafeSchemes(string url)
    {
        bool isSafe = SafeLauncher.IsSafeUrl(url, out var uri);
        Assert.True(isSafe);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("file://attacker-smb/exploit.exe")]
    [InlineData("cmd.exe /c calc.exe")]
    [InlineData("powershell.exe -enc AAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ms-settings:network")]
    [InlineData("")]
    [InlineData("   ")]
    public void SafeLauncher_BlocksUnsafeSchemes(string url)
    {
        bool isSafe = SafeLauncher.IsSafeUrl(url, out var uri);
        Assert.False(isSafe);
    }

    [Fact]
    public void SettingsService_ImportSettingsFromString_RejectsOversizedPayload()
    {
        // 2.5 MB of whitespace / JSON padding
        string oversized = "{\"Width\": 300, \"Padding\": \"" + new string('A', 2500000) + "\"}";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingsService.ImportSettingsFromString(oversized));

        Assert.Contains("2 MB", ex.Message);
    }

    [Fact]
    public void SettingsService_ImportSettingsFromString_NeverImportsCredentials()
    {
        string vnsWithCreds = """
        {
          "format": "vns",
          "fileVersion": 1,
          "settings": {
            "SettingsVersion": 13,
            "Width": 260,
            "YouTubeApiKey": "ATTACKER-KEY-12345",
            "SpotifySpDc": "ATTACKER-COOKIE-67890"
          }
        }
        """;

        var localSettings = new NotchSettings
        {
            YouTubeApiKey = "SAFE-LOCAL-KEY",
            SpotifySpDc = "SAFE-LOCAL-COOKIE"
        };

        var (imported, _) = SettingsService.ImportSettingsFromString(vnsWithCreds, localSettings);

        Assert.Equal("SAFE-LOCAL-KEY", imported.YouTubeApiKey);
        Assert.Equal("SAFE-LOCAL-COOKIE", imported.SpotifySpDc);
    }

    [Fact]
    public void SettingsService_NormalizeSettings_ClampsExtremeValues()
    {
        string malformed = """
        {
          "SettingsVersion": 13,
          "Width": 99999,
          "Height": -50,
          "CornerRadius": 1000,
          "AnimationFps": 10000,
          "NotificationDuration": 0,
          "ProcessPriority": "RealTimeRoot",
          "Language": "../../../etc/passwd",
          "ManualCity": "CityWithControlChars\\u0000\\r\\nBad"
        }
        """;

        var (imported, _) = SettingsService.ImportSettingsFromString(malformed);

        Assert.Equal(4000, imported.Width);
        Assert.Equal(20, imported.Height);
        Assert.Equal(50, imported.CornerRadius);
        Assert.Equal(AnimationConfig.MaxFps, imported.AnimationFps);
        Assert.Equal(1000, imported.NotificationDuration);
        Assert.Equal("Normal", imported.ProcessPriority);
        Assert.Equal("en", imported.Language);
        Assert.DoesNotContain('\0', imported.ManualCity);
        Assert.DoesNotContain('\r', imported.ManualCity);
        Assert.DoesNotContain('\n', imported.ManualCity);
    }

    [Theory]
    [InlineData(@"\\attacker-server\share\file.txt", true)]
    [InlineData(@"//attacker-server/share/file.txt", true)]
    [InlineData(@"C:\Users\User\file.txt", false)]
    [InlineData(@"D:\Folder\Subfolder\file.zip", false)]
    public void FileShelfController_IsUncPath_DetectsUncCorrectly(string path, bool expectedUnc)
    {
        bool isUnc = FileShelfController.IsUncPath(path);
        Assert.Equal(expectedUnc, isUnc);
    }

    [Fact]
    public void UpdateService_IsApprovedUpdate_RejectsUntrustedDomain()
    {
        var update = new UpdateInfo
        {
            Version = "2.0.0",
            DownloadUrl = "https://malicious-domain.com/downloads/V-Notch-Setup.exe",
            ManifestUrl = "https://malicious-domain.com/downloads/update-manifest.json",
            ManifestSignatureUrl = "https://malicious-domain.com/downloads/update-manifest.json.sig",
            InstallerName = "V-Notch-Setup.exe"
        };

        bool isApproved = UpdateService.IsApprovedUpdate(update);
        Assert.False(isApproved);
    }

    [Fact]
    public void UpdateService_IsApprovedUpdate_RejectsNonHttps()
    {
        var update = new UpdateInfo
        {
            Version = "2.0.0",
            DownloadUrl = "http://github.com/rainaku/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe",
            ManifestUrl = "https://github.com/rainaku/V-Notch/releases/download/v2.0.0/update-manifest.json",
            ManifestSignatureUrl = "https://github.com/rainaku/V-Notch/releases/download/v2.0.0/update-manifest.json.sig",
            InstallerName = "V-Notch-Setup.exe"
        };

        bool isApproved = UpdateService.IsApprovedUpdate(update);
        Assert.False(isApproved);
    }

    [Fact]
    public void UpdateService_IsApprovedUpdate_AcceptsOfficialGitHubReleases()
    {
        var update = new UpdateInfo
        {
            Version = "2.0.0",
            DownloadUrl = "https://github.com/rainaku/V-Notch/releases/download/v2.0.0/V-Notch-Setup.exe",
            ManifestUrl = "https://github.com/rainaku/V-Notch/releases/download/v2.0.0/update-manifest.json",
            ManifestSignatureUrl = "https://github.com/rainaku/V-Notch/releases/download/v2.0.0/update-manifest.json.sig",
            InstallerName = "V-Notch-Setup.exe"
        };

        bool isApproved = UpdateService.IsApprovedUpdate(update);
        Assert.True(isApproved);
    }

    [Fact]
    public void CrashReporter_FormatCrashReport_ScrubsSensitiveTokensAndUsername()
    {
        var ex = new InvalidOperationException("Failed request with sp_dc=AQB_secret_cookie_leak and AIzaSySecretApiKey12345");
        string report = CrashReporter.FormatCrashReport("TEST", ex, "ContextInfo", isTerminating: false);

        Assert.DoesNotContain("AQB_secret_cookie_leak", report);
        Assert.DoesNotContain("AIzaSySecretApiKey12345", report);
        Assert.Contains("[REDACTED]", report);
    }

    [Fact]
    public void EverythingSearchProvider_ParseReply_HandlesNonTerminatedBufferWithoutCrash()
    {
        // Construct a buffer that has numitems = 1, but the string has NO null terminator before byteCount
        int headerSize = 28;
        int itemSize = 12;
        int stringLength = 10;
        int totalBytes = headerSize + itemSize + stringLength * 2;

        IntPtr buffer = Marshal.AllocHGlobal(totalBytes);
        try
        {
            // Zero memory
            for (int i = 0; i < totalBytes; i++)
                Marshal.WriteByte(buffer, i, 0);

            // numitems offset = 20
            Marshal.WriteInt32(buffer, 20, 1);

            // Item at headerSize: flags = 0, nameOffset = headerSize + itemSize, pathOffset = headerSize + itemSize
            int itemOffset = headerSize;
            Marshal.WriteInt32(buffer, itemOffset, 0); // flags
            Marshal.WriteInt32(buffer, itemOffset + 4, itemOffset + itemSize); // nameOffset
            Marshal.WriteInt32(buffer, itemOffset + 8, itemOffset + itemSize); // pathOffset

            // Fill string with non-zero chars ('A') all the way to the end of totalBytes (NO null terminator)
            for (int i = itemOffset + itemSize; i < totalBytes; i += 2)
            {
                Marshal.WriteInt16(buffer, i, (short)'A');
            }

            // ParseReply should safely read up to byteCount and not crash or over-read unmapped memory
            var results = EverythingSearchProvider.ParseReply(buffer, totalBytes);

            Assert.NotNull(results);
            Assert.Single(results);
            Assert.Equal(new string('A', stringLength), results[0].Item1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
