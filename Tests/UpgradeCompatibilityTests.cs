using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UpgradeCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vnotch-upgrade-" + Guid.NewGuid().ToString("N"));

    public UpgradeCompatibilityTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void UpdateInstallerExplicitlyRequestsTheUpdateFlow()
    {
        var startInfo = UpdateService.CreateInstallerStartInfo(Path.Combine(_directory, "V-Notch-Setup.exe"), _directory);
        Assert.Equal("--update", startInfo.Arguments);
        Assert.Equal(_directory, startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void LockedMigrationKeepsPreferencesAndRetriesAfterTheFileIsReleased()
    {
        string path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new NotchSettings
        {
            SettingsVersion = 13,
            Language = "vi",
            Width = 270,
            EnableSpotlight = false
        }));
        var original = File.ReadAllBytes(path);
        using var service = new SettingsService(path, _ => { });
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var loaded = service.Load();
            Assert.Equal("vi", loaded.Language);
            Assert.Equal(270, loaded.Width);
            Assert.False(loaded.EnableSpotlight);
            Assert.Equal(original, File.ReadAllBytes(path));
        }

        var retried = service.Load();
        Assert.Equal("vi", retried.Language);
        Assert.Equal(270, retried.Width);
        Assert.False(retried.EnableSpotlight);
        var stored = JsonSerializer.Deserialize<NotchSettings>(File.ReadAllText(path))!;
        Assert.Equal(SettingsMigrator.CurrentVersion, stored.SettingsVersion);
        Assert.Equal("vi", stored.Language);
    }

    [Fact]
    public async Task UpgradeCopiesTheNewPayloadWithoutReplacingSavedPreferences()
    {
        string source = Directory.CreateDirectory(Path.Combine(_directory, "payload")).FullName;
        string installed = Directory.CreateDirectory(Path.Combine(_directory, "installed")).FullName;
        string path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(Path.Combine(source, "V-Notch.exe"), "new app payload");
        await File.WriteAllTextAsync(Path.Combine(installed, "V-Notch.exe"), "old app payload");
        await using (var previous = new SettingsService(path))
        {
            await previous.SaveAsync(new NotchSettings { Language = "vi", Width = 270, EnableSpotlight = false });
        }
        byte[] original = await File.ReadAllBytesAsync(path);
        await SetupOperations.InstallAsync(new(source, installed, false, "en"), _ => { },
            new(() => Task.CompletedTask, (_, _) => { }, (_, _) => { }, (_, _) => { },
                language => SetupOperations.InitializeSettingsFile(path, language)));

        Assert.Equal("new app payload", await File.ReadAllTextAsync(Path.Combine(installed, "V-Notch.exe")));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        using var updated = new SettingsService(path);
        var loaded = updated.Load();
        Assert.Equal("vi", loaded.Language);
        Assert.Equal(270, loaded.Width);
        Assert.False(loaded.EnableSpotlight);
    }

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

    [Theory]
    [InlineData("InstallLocation")]
    [InlineData("Inno Setup: App Path")]
    public void SetupFindsLegacyInnoInstallationsWithoutRunningTheirUninstallCommand(string valueName)
    {
        File.WriteAllText(Path.Combine(_directory, "V-Notch.exe"), "legacy fixture executable");
        string registryPath = @"Software\VNotch.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(registryPath);
            using (var legacy = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B2567AF4-8BAE-46D2-B83E-56539C82F888}_is1"))
            {
                legacy.SetValue(valueName, _directory);
                legacy.SetValue("UninstallString", "untrusted registry command; must never execute");
            }
            Assert.Equal(_directory, SetupOperations.SelectInstallDirectory(SetupOperations.ReadInstallDirectories(root), "fallback"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
    }

    [Fact]
    public void InstalledVersionDoesNotOfferItselfAgain()
    {
        using var service = new UpdateService();
        Assert.Equal(AppIntegrityService.GetAppVersion(), service.CurrentVersion);
        string releaseVersion = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal(0, UpdateService.CompareVersions(releaseVersion, service.CurrentVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StableSetupPreservesBetaSettingsAndUpdatePreference(bool includePrereleases)
    {
        var path = Path.Combine(_directory, "settings.json");
        var beta = new NotchSettings
        {
            SettingsVersion = SettingsMigrator.CurrentVersion,
            LastRunVersion = "2.0.1-beta.184.1",
            IncludePrereleaseUpdates = includePrereleases,
            Language = "vi",
            Width = 270,
            EnableSpotlight = false
        };
        File.WriteAllText(path, JsonSerializer.Serialize(beta));
        var original = File.ReadAllBytes(path);

        SetupOperations.InitializeSettingsFile(path, "en");
        Assert.Equal(original, File.ReadAllBytes(path));
        using var service = new SettingsService(path);
        var settings = service.Load();
        Assert.Equal(includePrereleases, settings.IncludePrereleaseUpdates);
        Assert.Equal(beta.LastRunVersion, settings.LastRunVersion);
        Assert.Equal("vi", settings.Language);
        Assert.Equal(270, settings.Width);
        Assert.False(settings.EnableSpotlight);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
