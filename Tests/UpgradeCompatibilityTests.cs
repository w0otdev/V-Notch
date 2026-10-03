using System.IO;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UpgradeCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vnotch-upgrade-" + Guid.NewGuid().ToString("N"));

    public UpgradeCompatibilityTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SetupPreserves193SettingsThenApplicationMigratesWithConsentRequired()
    {
        var path = Path.Combine(_directory, "settings.json");
        var apiKey = DataProtection.Protect("legacy-test-api-key");
        var cookie = DataProtection.Protect("legacy-test-cookie");
        var legacy = new NotchSettings
        {
            SettingsVersion = 13,
            Language = "vi",
            Width = 270,
            EnableSpotlight = false,
            EnableSpotifyCanvas = true,
            AllowOnlineCanvas = true,
            YouTubeApiKey = apiKey,
            SpotifySpDc = cookie
        };
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));
        var original = File.ReadAllBytes(path);

        SetupOperations.InitializeSettingsFile(path, "en");
        Assert.Equal(original, File.ReadAllBytes(path));
        using var service = new SettingsService(path);
        var migrated = service.Load();
        Assert.Equal(SettingsMigrator.CurrentVersion, migrated.SettingsVersion);
        Assert.Equal("vi", migrated.Language);
        Assert.Equal(270, migrated.Width);
        Assert.False(migrated.EnableSpotlight);
        Assert.Equal(apiKey, migrated.YouTubeApiKey);
        Assert.Equal(cookie, migrated.SpotifySpDc);
        Assert.False(NetworkPrivacy.Allows(migrated, NetworkFeature.Canvas));
        Assert.False(SpotifyCanvasConsent.HasAccepted(migrated));
        Assert.Equal("legacy-test-api-key", DataProtection.Unprotect(migrated.YouTubeApiKey));
        Assert.Equal("legacy-test-cookie", DataProtection.Unprotect(migrated.SpotifySpDc));
    }

    [Fact]
    public void SetupDoesNotOverwriteAnExistingUnreadableSettingsFile()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ interrupted legacy settings");
        SetupOperations.InitializeSettingsFile(path, "en");
        Assert.Equal("{ interrupted legacy settings", File.ReadAllText(path));
    }

    [Fact]
    public void FreshSetupCreatesCurrentSettingsInSelectedLanguage()
    {
        var path = Path.Combine(_directory, "settings.json");
        SetupOperations.InitializeSettingsFile(path, "vi");
        var settings = JsonSerializer.Deserialize<NotchSettings>(File.ReadAllText(path))!;
        Assert.Equal("vi", settings.Language);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.False(NetworkPrivacy.Allows(settings, NetworkFeature.Canvas));
    }

    [Fact]
    public void SetupReusesCustomInstallDirectoryAndIgnoresInvalidRegistryPaths()
    {
        File.WriteAllText(Path.Combine(_directory, "V-Notch.exe"), "test placeholder; never executed");
        var candidates = new[] { "cmd.exe /c untrusted", Path.GetPathRoot(_directory), _directory };
        Assert.Equal(_directory, SetupOperations.SelectInstallDirectory(candidates, "fallback"));
        Assert.Equal("fallback", SetupOperations.SelectInstallDirectory([Path.Combine(_directory, "missing")], "fallback"));
    }

    [Fact]
    public void InstalledVersionDoesNotOfferItselfAgain()
    {
        var service = new UpdateService();
        Assert.Equal(AppIntegrityService.GetAppVersion(), service.CurrentVersion);
        string releaseVersion = typeof(UpdateService).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(0, UpdateService.CompareVersions(releaseVersion, service.CurrentVersion));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
