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
          "GpuPreference": -5,
          "LiquidGlass": {
            "BlurAmount": -1.0,
            "Refraction": -2.0,
            "EdgeBend": -5.0,
            "ChromaticAberration": -1.0,
            "EdgeHighlight": -1.0,
            "TouchLight": -2.0,
            "Specular": -1.0,
            "Fresnel": -1.0,
            "Distortion": -1.0,
            "CornerRadius": -10,
            "ZRadius": -0.5,
            "Opacity": -1.0,
            "Saturation": -5.0,
            "Brightness": -5.0,
            "ShadowOpacity": -1.0,
            "ShadowSpread": -10,
            "BevelMode": -2,
            "TargetFps": -60,
            "Variant": -1,
            "PowerFactor": -3.0,
            "RefractionA": -1.0,
            "RefractionB": -1.0,
            "RefractionC": -1.0,
            "RefractionD": -1.0,
            "FPower": -2.0,
            "Noise": -0.5,
            "GlowWeight": -10.0,
            "GlowBias": -10.0,
            "GlowEdge0": -10.0,
            "GlowEdge1": -10.0
          }
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

        Assert.NotNull(s.LiquidGlass);
        Assert.Equal(0.0, s.LiquidGlass.BlurAmount, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.Refraction, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.EdgeBend, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.ChromaticAberration, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.EdgeHighlight, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.TouchLight, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.Specular, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.Fresnel, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.Distortion, precision: 6);
        Assert.Equal(0, s.LiquidGlass.CornerRadius);
        Assert.Equal(0.05, s.LiquidGlass.ZRadius, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.Opacity, precision: 6);
        Assert.Equal(-1.0, s.LiquidGlass.Saturation, precision: 6);
        Assert.Equal(-0.5, s.LiquidGlass.Brightness, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.ShadowOpacity, precision: 6);
        Assert.Equal(0, s.LiquidGlass.ShadowSpread);
        Assert.Equal(0, s.LiquidGlass.BevelMode);
        Assert.Equal(0, s.LiquidGlass.TargetFps);
        Assert.Equal(0, s.LiquidGlass.Variant);
        Assert.Equal(0.5, s.LiquidGlass.PowerFactor, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.RefractionA, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.RefractionB, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.RefractionC, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.RefractionD, precision: 6);
        Assert.Equal(0.1, s.LiquidGlass.FPower, precision: 6);
        Assert.Equal(0.0, s.LiquidGlass.Noise, precision: 6);
        Assert.Equal(-2.0, s.LiquidGlass.GlowWeight, precision: 6);
        Assert.Equal(-2.0, s.LiquidGlass.GlowBias, precision: 6);
        Assert.Equal(-2.0, s.LiquidGlass.GlowEdge0, precision: 6);
        Assert.Equal(-2.0, s.LiquidGlass.GlowEdge1, precision: 6);
    }

    [Fact]
    public void ImportSettingsFromString_ClampsHugeOutOfRangeValuesAndLiquidGlassCustom()
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
            "LiquidGlass": {
              "BlurAmount": 99.0,
              "Refraction": 99.0,
              "EdgeBend": 999.0,
              "ChromaticAberration": 99.0,
              "EdgeHighlight": 99.0,
              "TouchLight": 99.0,
              "Specular": 99.0,
              "Fresnel": 99.0,
              "Distortion": 99.0,
              "CornerRadius": 999,
              "ZRadius": 99.0,
              "Opacity": 99.0,
              "Saturation": 99.0,
              "Brightness": 99.0,
              "ShadowOpacity": 99.0,
              "ShadowSpread": 999,
              "BevelMode": 99,
              "TargetFps": 9999,
              "Variant": 99,
              "PowerFactor": 999.0,
              "RefractionA": 999.0,
              "RefractionB": 999.0,
              "RefractionC": 999.0,
              "RefractionD": 999.0,
              "FPower": 999.0,
              "Noise": 99.0,
              "GlowWeight": 99.0,
              "GlowBias": 99.0,
              "GlowEdge0": 99.0,
              "GlowEdge1": 99.0
            },
            "LiquidGlassCustom": {
              "BlurAmount": 50.0,
              "Refraction": -5.0,
              "ShadowSpread": 500,
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

        Assert.Equal(1.0, s.LiquidGlass.BlurAmount, precision: 6);
        Assert.Equal(1.5, s.LiquidGlass.Refraction, precision: 6);
        Assert.Equal(10.0, s.LiquidGlass.EdgeBend, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.ChromaticAberration, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.EdgeHighlight, precision: 6);
        Assert.Equal(3.0, s.LiquidGlass.TouchLight, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.Specular, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.Fresnel, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.Distortion, precision: 6);
        Assert.Equal(50, s.LiquidGlass.CornerRadius);
        Assert.Equal(0.60, s.LiquidGlass.ZRadius, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.Opacity, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.Saturation, precision: 6);
        Assert.Equal(0.5, s.LiquidGlass.Brightness, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.ShadowOpacity, precision: 6);
        Assert.Equal(40, s.LiquidGlass.ShadowSpread);
        Assert.Equal(1, s.LiquidGlass.BevelMode);
        Assert.Equal(240, s.LiquidGlass.TargetFps);
        Assert.Equal(1, s.LiquidGlass.Variant);
        Assert.Equal(10.0, s.LiquidGlass.PowerFactor, precision: 6);
        Assert.Equal(20.0, s.LiquidGlass.RefractionA, precision: 6);
        Assert.Equal(20.0, s.LiquidGlass.RefractionB, precision: 6);
        Assert.Equal(20.0, s.LiquidGlass.RefractionC, precision: 6);
        Assert.Equal(20.0, s.LiquidGlass.RefractionD, precision: 6);
        Assert.Equal(10.0, s.LiquidGlass.FPower, precision: 6);
        Assert.Equal(1.0, s.LiquidGlass.Noise, precision: 6);
        Assert.Equal(2.0, s.LiquidGlass.GlowWeight, precision: 6);
        Assert.Equal(2.0, s.LiquidGlass.GlowBias, precision: 6);
        Assert.Equal(2.0, s.LiquidGlass.GlowEdge0, precision: 6);
        Assert.Equal(2.0, s.LiquidGlass.GlowEdge1, precision: 6);

        Assert.NotNull(s.LiquidGlassCustom);
        Assert.Equal(1.0, s.LiquidGlassCustom!.BlurAmount, precision: 6);
        Assert.Equal(0.0, s.LiquidGlassCustom.Refraction, precision: 6);
        Assert.Equal(40, s.LiquidGlassCustom.ShadowSpread);
        Assert.Equal(0, s.LiquidGlassCustom.TargetFps);
    }

    [Fact]
    public void LoadSettings_ClampsOutOfRangeAndNullLiquidGlass_AndPersistsNormalizedValues()
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
          "LiquidGlass": null
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
        Assert.NotNull(loaded.LiquidGlass);
        Assert.Equal(0.3, loaded.LiquidGlass.BlurAmount, precision: 6);

        var persisted = JsonSerializer.Deserialize<NotchSettings>(File.ReadAllText(path));
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
            DebugWindowY = double.PositiveInfinity,
            LiquidGlass = new LiquidGlassConfig
            {
                BlurAmount = double.NaN,
                Refraction = double.PositiveInfinity,
                EdgeBend = double.NegativeInfinity,
                ChromaticAberration = double.NaN,
                Noise = double.NaN,
                Opacity = double.NaN
            }
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
        Assert.Equal(0.3, settings.LiquidGlass.BlurAmount, precision: 6);
        Assert.Equal(1.0, settings.LiquidGlass.Refraction, precision: 6);
        Assert.Equal(1.65, settings.LiquidGlass.EdgeBend, precision: 6);
        Assert.Equal(0.56, settings.LiquidGlass.ChromaticAberration, precision: 6);
        Assert.Equal(0.066, settings.LiquidGlass.Noise, precision: 6);
        Assert.Equal(1.0, settings.LiquidGlass.Opacity, precision: 6);

        // Second pass on already-normalized settings must report no changes
        bool changedAgain = (bool)method.Invoke(null, [settings])!;
        Assert.False(changedAgain);
    }
}
