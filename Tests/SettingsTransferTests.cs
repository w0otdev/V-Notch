using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class SettingsTransferTests : IDisposable
{
    private readonly string _tempFolder;
    private readonly SettingsService _settingsService;

    public SettingsTransferTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "VNotch_Transfer_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
        var settingsPath = Path.Combine(_tempFolder, "settings.json");
        _settingsService = new SettingsService(settingsPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, true);
        }
        catch { }
    }

    [Fact]
    public void ExportSettingsToString_ContainsVnsEnvelope()
    {
        var settings = new NotchSettings
        {
            Width = 320,
            Height = 36,
            CornerRadius = 14,
            ExpandedWidget = "weather",
            Language = "vi"
        };

        string exportedJson = _settingsService.ExportSettingsToString(settings);

        Assert.NotNull(exportedJson);
        var node = JsonNode.Parse(exportedJson);
        Assert.NotNull(node);
        Assert.Equal("vns", node["format"]?.GetValue<string>());
        Assert.Equal(1, node["fileVersion"]?.GetValue<int>());
        Assert.NotNull(node["appVersion"]?.GetValue<string>());
        Assert.NotNull(node["exportedAt"]?.GetValue<string>());

        var inner = node["settings"];
        Assert.NotNull(inner);
        Assert.Equal(320, inner[nameof(NotchSettings.Width)]?.GetValue<int>());
        Assert.Equal("weather", inner[nameof(NotchSettings.ExpandedWidget)]?.GetValue<string>());
        Assert.Equal("vi", inner[nameof(NotchSettings.Language)]?.GetValue<string>());
    }

    [Fact]
    public void ExportSettingsToString_ExportsPlaintextKeysForPortability()
    {
        var settings = new NotchSettings
        {
            YouTubeApiKey = "TEST-YOUTUBE-API-KEY-12345",
            SpotifySpDc = "TEST-SP-DC-COOKIE-67890"
        };

        string exportedJson = _settingsService.ExportSettingsToString(settings);

        var node = JsonNode.Parse(exportedJson);
        Assert.NotNull(node);
        var inner = node["settings"];
        Assert.NotNull(inner);

        // Keys in the .vns file must be portable (unencrypted plaintext so another machine can DPAPI-protect them)
        Assert.Equal("TEST-YOUTUBE-API-KEY-12345", inner[nameof(NotchSettings.YouTubeApiKey)]?.GetValue<string>());
        Assert.Equal("TEST-SP-DC-COOKIE-67890", inner[nameof(NotchSettings.SpotifySpDc)]?.GetValue<string>());
    }

    [Fact]
    public void ImportSettingsFromString_EnvelopeFormat_RestoresAllSettings()
    {
        var original = new NotchSettings
        {
            Width = 280,
            DynamicIslandWidth = 300,
            Height = 38,
            CornerRadius = 12,
            ExpandedWidget = "calendar",
            Language = "es",
            EnableHelloGreeting = false,
            YouTubeApiKey = "MY-EXPORTED-KEY"
        };

        string exported = _settingsService.ExportSettingsToString(original);

        var (imported, requiresRestart) = _settingsService.ImportSettingsFromString(exported, new NotchSettings());

        Assert.NotNull(imported);
        Assert.Equal(280, imported.Width);
        Assert.Equal(300, imported.DynamicIslandWidth);
        Assert.Equal(38, imported.Height);
        Assert.Equal(12, imported.CornerRadius);
        Assert.Equal("calendar", imported.ExpandedWidget);
        Assert.Equal("es", imported.Language);
        Assert.False(imported.EnableHelloGreeting);
        Assert.Equal("MY-EXPORTED-KEY", imported.YouTubeApiKey);
        Assert.False(requiresRestart);
    }

    [Fact]
    public void ImportSettingsFromString_DirectJsonFormat_RestoresAllSettings()
    {
        const string rawJson = """
            {
              "SettingsVersion": 13,
              "Width": 260,
              "DynamicIslandWidth": 280,
              "Language": "fr",
              "ExpandedWidget": "media"
            }
            """;

        var (imported, requiresRestart) = _settingsService.ImportSettingsFromString(rawJson, new NotchSettings());

        Assert.NotNull(imported);
        Assert.Equal(260, imported.Width);
        Assert.Equal(280, imported.DynamicIslandWidth);
        Assert.Equal("fr", imported.Language);
        Assert.Equal("media", imported.ExpandedWidget);
        Assert.False(requiresRestart);
    }

    [Fact]
    public void ImportSettingsFromString_LegacyVersion_MigratesAndNormalizes()
    {
        const string legacyEnvelope = """
            {
              "format": "vns",
              "fileVersion": 1,
              "settings": {
                "SettingsVersion": 3,
                "Width": 240,
                "Language": "en"
              }
            }
            """;

        var (imported, _) = _settingsService.ImportSettingsFromString(legacyEnvelope);

        Assert.NotNull(imported);
        Assert.Equal(SettingsMigrator.CurrentVersion, imported.SettingsVersion);
        Assert.Equal(240, imported.Width);
        Assert.Equal(270, imported.DynamicIslandWidth);
        Assert.True(imported.EnableBlurEffects);
    }

    [Fact]
    public void ExportAndImportFromFile_RoundTripSucceeds()
    {
        var settings = new NotchSettings
        {
            Width = 310,
            Height = 42,
            CornerRadius = 18,
            ExpandedWidget = "weather",
            AnimationFps = 120,
            Language = "vi"
        };

        var filePath = Path.Combine(_tempFolder, "my_backup.vns");
        _settingsService.ExportSettingsToFile(filePath, settings);

        Assert.True(File.Exists(filePath));

        var (imported, _) = _settingsService.ImportSettingsFromFile(filePath);

        Assert.Equal(310, imported.Width);
        Assert.Equal(42, imported.Height);
        Assert.Equal(18, imported.CornerRadius);
        Assert.Equal("weather", imported.ExpandedWidget);
        Assert.Equal(120, imported.AnimationFps);
        Assert.Equal("vi", imported.Language);
    }

    [Fact]
    public void CheckRequiresRestart_DetectsGpuPreferenceChange()
    {
        var current = new NotchSettings { GpuPreference = 0 };
        var imported = new NotchSettings { GpuPreference = 2 };

        bool requires = SettingsService.CheckRequiresRestart(imported, current);

        Assert.True(requires);
    }

    [Fact]
    public void CheckRequiresRestart_DetectsProcessPriorityChange()
    {
        var current = new NotchSettings { ProcessPriority = "Normal" };
        var imported = new NotchSettings { ProcessPriority = "High" };

        bool requires = SettingsService.CheckRequiresRestart(imported, current);

        Assert.True(requires);
    }

    [Fact]
    public void CheckRequiresRestart_SameSettings_ReturnsFalse()
    {
        var current = new NotchSettings { GpuPreference = 1, ProcessPriority = "High" };
        var imported = new NotchSettings { GpuPreference = 1, ProcessPriority = "High" };

        bool requires = SettingsService.CheckRequiresRestart(imported, current);

        Assert.False(requires);
    }

    [Fact]
    public void ImportSettingsFromString_CorruptJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => _settingsService.ImportSettingsFromString("{ corrupt json ..."));
    }

    [Fact]
    public void ImportSettingsFromString_EmptyString_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => _settingsService.ImportSettingsFromString(""));
    }
}
