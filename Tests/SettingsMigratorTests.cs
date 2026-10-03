using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SettingsMigratorTests
{
    [Fact]
    public void CanvasIsOffOnNewInstall()
    {
        var settings = new NotchSettings();
        Assert.False(settings.EnableSpotifyCanvas);
        Assert.False(settings.AllowOnlineCanvas);
        Assert.False(SpotifyCanvasConsent.HasAccepted(settings));
    }

    [Theory]
    [InlineData(13, 1)] // Even an injected consent field in a legacy file is reset.
    [InlineData(14, 0)]
    [InlineData(14, 2)]
    public void MigrationNeverInfersConsentFromEnabledFlags(int version, int consentVersion)
    {
        string json = $$"""
            { "SettingsVersion": {{version}}, "EnableSpotifyCanvas": true,
              "AllowOnlineCanvas": true, "SpotifyCanvasConsentVersion": {{consentVersion}} }
            """;
        var (settings, migrated) = SettingsMigrator.Migrate(json);
        Assert.True(migrated);
        Assert.False(settings.EnableSpotifyCanvas);
        Assert.False(settings.AllowOnlineCanvas);
        Assert.Equal(0, settings.SpotifyCanvasConsentVersion);
    }

    [Fact]
    public void AcceptedCanvasSettingsSurviveLocalSaveAndReload()
    {
        var settings = new NotchSettings { SettingsVersion = SettingsMigrator.CurrentVersion };
        Assert.True(SpotifyCanvasConsent.TryEnable(settings, () => true));
        var (loaded, migrated) = SettingsMigrator.Migrate(JsonSerializer.Serialize(settings));
        Assert.False(migrated);
        Assert.True(NetworkPrivacy.Allows(loaded, NetworkFeature.Canvas));
    }

    [Fact]
    public void Migrate_Version3_AddsDynamicIslandWidthFromExistingWidth()
    {
        const string rawJson = """
            {
              "SettingsVersion": 3,
              "Width": 230
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(260, settings.DynamicIslandWidth);
    }

    [Fact]
    public void Migrate_PreservesExistingDynamicIslandWidth()
    {
        const string rawJson = """
            {
              "SettingsVersion": 3,
              "Width": 230,
              "DynamicIslandWidth": 320
            }
            """;

        var (settings, _) = SettingsMigrator.Migrate(rawJson);

        Assert.Equal(320, settings.DynamicIslandWidth);
    }

    [Fact]
    public void NewDefaults_UseSliderAlignedDynamicIslandWidth()
    {
        var settings = new NotchSettings();

        Assert.Equal(220, settings.DynamicIslandWidth);
    }

    [Fact]
    public void NewDefaults_UseReadableSpotifyCanvasBrightness()
    {
        var settings = new NotchSettings();

        Assert.Equal(0.7, settings.SpotifyCanvasBrightness);
    }

    [Fact]
    public void NewDefaults_AllowHighRefreshDisplays()
    {
        var settings = new NotchSettings();

        Assert.Equal(240, settings.AnimationFps);
    }

    [Fact]
    public void NewDefaults_EnableGpuGlassWithoutHidingNotchFromCapture()
    {
        var settings = new NotchSettings();

        Assert.True(settings.LiquidGlass.UseGpuRefraction);
        Assert.False(settings.LiquidGlass.HideFromScreenCapture);
    }

    [Fact]
    public void CurrentSettings_PreserveExplicitGpuOptOut()
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{SettingsMigrator.CurrentVersion}},
              "LiquidGlass": {
                "UseGpuRefraction": false
              }
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.False(migrated);
        Assert.False(settings.LiquidGlass.UseGpuRefraction);
    }

    [Fact]
    public void Migrate_Version4_AddsPerformanceDefaults()
    {
        const string rawJson = """
            {
              "SettingsVersion": 4
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.EnableBlurEffects);
        Assert.Equal(AnimationConfig.MaxFps, settings.AnimationFps);
    }

    [Fact]
    public void Migrate_Version10_PreservesSupportedHighRefreshRate()
    {
        const string rawJson = """
            {
              "SettingsVersion": 10,
              "AnimationFps": 144
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(144, settings.AnimationFps);
    }

    [Fact]
    public void Migrate_Version11_EnablesSpotlightByDefault()
    {
        const string rawJson = """
            {
              "SettingsVersion": 11
            }
            """;

        var (settings, migrated) = SettingsMigrator.Migrate(rawJson);

        Assert.True(migrated);
        Assert.Equal(SettingsMigrator.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.EnableSpotlight);
    }

    [Fact]
    public void Migrate_Version11_PreservesExplicitSpotlightOptOut()
    {
        const string rawJson = """
            {
              "SettingsVersion": 11,
              "EnableSpotlight": false
            }
            """;

        var (settings, _) = SettingsMigrator.Migrate(rawJson);

        Assert.False(settings.EnableSpotlight);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(SettingsMigrator.CurrentVersion + 1)]
    public void Migrate_InvalidOrFutureVersion_ThrowsJsonException(int invalidVersion)
    {
        string rawJson = $$"""
            {
              "SettingsVersion": {{invalidVersion}}
            }
            """;

        var ex = Assert.Throws<JsonException>(() => SettingsMigrator.Migrate(rawJson));
        Assert.Contains($"Unsupported settings version: {invalidVersion}", ex.Message);
    }
}
