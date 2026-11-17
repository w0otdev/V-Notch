using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsNormalizationImportTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsNormalizationImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VNotch_NormImport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of temp directory
        }
    }

    [Fact]
    public void ImportSettingsFromString_ClampsAnimationSpeedHoverCollapseDelayAndGpuPreference()
    {
        var (settings, _) = SettingsService.ImportSettingsFromString(
            "{\"AnimationSpeed\":-5,\"HoverCollapseDelay\":-100,\"GpuPreference\":99}");

        Assert.Equal(0.5, settings.AnimationSpeed, precision: 6);
        Assert.Equal(100, settings.HoverCollapseDelay);
        Assert.Equal(2, settings.GpuPreference);
    }

    [Fact]
    public void ImportSettingsFromString_ClampsNegativeAndLowOutOfRangeValues()
    {
        const string json = """
        {
          "SettingsVersion": 14,
          "AnimationSpeed": -10.0,
          "HoverExpandDelay": -50,
          "HoverCollapseDelay": 0,
          "HoverZoneMargin": -20,
          "IdleAutoHideDelay": -500,
          "MediaBlurBrightnessBoost": -3.0,
          "MediaBlurDarkOverlay": -1.5,
          "CompactExpandMultiplier": 0.1,
          "MediumExpandMultiplier": 0.5,
          "LargeExpandMultiplier": -2.0,
          "SpotlightAiWordsPerSecond": -10,
          "GpuPreference": -5
        }
        """;

        var (s, _) = SettingsService.ImportSettingsFromString(json);

        Assert.Equal(0.5, s.AnimationSpeed, precision: 6);
        Assert.Equal(0, s.HoverExpandDelay);
        Assert.Equal(100, s.HoverCollapseDelay);
        Assert.Equal(10, s.HoverZoneMargin);
        Assert.Equal(1, s.IdleAutoHideDelay);
        Assert.Equal(0.0, s.MediaBlurBrightnessBoost, precision: 6);
        Assert.Equal(0.0, s.MediaBlurDarkOverlay, precision: 6);
        Assert.Equal(1.0, s.CompactExpandMultiplier, precision: 6);
        Assert.Equal(1.4, s.MediumExpandMultiplier, precision: 6);
        Assert.Equal(2.0, s.LargeExpandMultiplier, precision: 6);
        Assert.Equal(2, s.SpotlightAiWordsPerSecond);
        Assert.Equal(0, s.GpuPreference);
    }

    [Fact]
    public void ImportSettingsFromString_ClampsHugeOutOfRangeValuesAndIgnoresLegacyLiquidGlass()
    {
        const string json = """
        {
          "format": "vns",
          "fileVersion": 1,
          "settings": {
            "SettingsVersion": 14,
            "AnimationSpeed": 999.0,
            "HoverExpandDelay": 50000,
            "HoverCollapseDelay": 99999,
            "HoverZoneMargin": 5000,
            "IdleAutoHideDelay": 999999,
            "MediaBlurBrightnessBoost": 50.0,
            "MediaBlurDarkOverlay": 25.0,
            "CompactExpandMultiplier": 10.0,
            "MediumExpandMultiplier": 20.0,
            "LargeExpandMultiplier": 50.0,
            "SpotlightAiWordsPerSecond": 5000,
            "GpuPreference": 42,
            "NotchStyle": "liquidglass",
            "LiquidGlassPreset": "frosted",
            "LiquidGlass": {
              "BlurAmount": 99.0,
              "UseUltimateGpu": true
            },
            "LiquidGlassCustom": {
              "BlurAmount": 50.0,
              "TargetFps": -10
            }
          }
        }
        """;

        var (s, _) = SettingsService.ImportSettingsFromString(json);

        Assert.Equal(2.0, s.AnimationSpeed, precision: 6);
        Assert.Equal(500, s.HoverExpandDelay);
        Assert.Equal(2000, s.HoverCollapseDelay);
        Assert.Equal(100, s.HoverZoneMargin);
        Assert.Equal(60000, s.IdleAutoHideDelay);
        Assert.Equal(2.0, s.MediaBlurBrightnessBoost, precision: 6);
        Assert.Equal(1.0, s.MediaBlurDarkOverlay, precision: 6);
        Assert.Equal(1.6, s.CompactExpandMultiplier, precision: 6);
        Assert.Equal(2.6, s.MediumExpandMultiplier, precision: 6);
        Assert.Equal(4.0, s.LargeExpandMultiplier, precision: 6);
        Assert.Equal(30, s.SpotlightAiWordsPerSecond);
        Assert.Equal(2, s.GpuPreference);
        var exported = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(s))!.AsObject();
        Assert.False(exported.ContainsKey("NotchStyle"));
        Assert.False(exported.ContainsKey("LiquidGlass"));
    }

    [Fact]
    public void LoadSettings_ClampsOutOfRangeValues_DropsLegacyLiquidGlass_AndPersistsNormalizedValues()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, """
        {
          "SettingsVersion": 14,
          "AnimationSpeed": -3.0,
          "HoverExpandDelay": 9999,
          "HoverCollapseDelay": 10,
          "HoverZoneMargin": 500,
          "IdleAutoHideDelay": 0,
          "SpotlightAiWordsPerSecond": 999,
          "GpuPreference": -9,
          "NotchStyle": "liquidglass",
          "LiquidGlassPreset": "custom",
          "LiquidGlass": { "BlurAmount": 0.4, "UseGpuRefraction": true },
          "LiquidGlassCustom": null
        }
        """);

        using var service = new SettingsService(path, _ => { });
        NotchSettings loaded = service.Load();

        Assert.Equal(0.5, loaded.AnimationSpeed, precision: 6);
        Assert.Equal(500, loaded.HoverExpandDelay);
        Assert.Equal(100, loaded.HoverCollapseDelay);
        Assert.Equal(100, loaded.HoverZoneMargin);
        Assert.Equal(1, loaded.IdleAutoHideDelay);
        Assert.Equal(30, loaded.SpotlightAiWordsPerSecond);
        Assert.Equal(0, loaded.GpuPreference);

        string persistedJson = File.ReadAllText(path);
        Assert.DoesNotContain("LiquidGlass", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("NotchStyle", persistedJson, StringComparison.Ordinal);
        var persisted = JsonSerializer.Deserialize<NotchSettings>(persistedJson);
        Assert.NotNull(persisted);
        Assert.Equal(0.5, persisted!.AnimationSpeed, precision: 6);
        Assert.Equal(100, persisted.HoverCollapseDelay);
        Assert.Equal(0, persisted.GpuPreference);
    }

    [Fact]
    public void NormalizeSettings_SanitizesNaNAndInfinityFieldsToDefaults()
    {
        var method = typeof(SettingsService).GetMethod("NormalizeSettings", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var settings = new NotchSettings
        {
            AnimationSpeed = double.NaN,
            MediaBlurBrightnessBoost = double.PositiveInfinity,
            MediaBlurDarkOverlay = double.NegativeInfinity,
            CompactExpandMultiplier = double.NaN,
            MediumExpandMultiplier = double.PositiveInfinity,
            LargeExpandMultiplier = double.NegativeInfinity,
            DebugWindowX = double.NaN,
            DebugWindowY = double.PositiveInfinity
        };

        bool changed = (bool)method!.Invoke(null, [settings])!;
        Assert.True(changed);

        Assert.Equal(2.0, settings.AnimationSpeed, precision: 6);
        Assert.Equal(2.0, settings.MediaBlurBrightnessBoost, precision: 6);
        Assert.Equal(0.0, settings.MediaBlurDarkOverlay, precision: 6);
        Assert.Equal(1.2, settings.CompactExpandMultiplier, precision: 6);
        Assert.Equal(1.8, settings.MediumExpandMultiplier, precision: 6);
        Assert.Equal(2.5, settings.LargeExpandMultiplier, precision: 6);
        Assert.Null(settings.DebugWindowX);
        Assert.Null(settings.DebugWindowY);

        // Second pass on already-normalized settings must report no changes
        bool changedAgain = (bool)method.Invoke(null, [settings])!;
        Assert.False(changedAgain);
    }
}
