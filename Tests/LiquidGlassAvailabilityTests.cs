using System.IO;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LiquidGlassAvailabilityTests
{
    [Fact]
    public void Load_PreservesLiquidGlassWhenDynamicIslandModeIsOff()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vnotch-glass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");

        try
        {
            var settings = new NotchSettings
            {
                SettingsVersion = SettingsMigrator.CurrentVersion,
                EnableDynamicIslandMode = false,
                NotchStyle = "liquidglass"
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings));

            NotchSettings loaded = new SettingsService(path, _ => { }).Load();

            Assert.Equal("liquidglass", loaded.NotchStyle);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_PreservesLiquidGlassWhenDynamicIslandModeIsOn()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vnotch-glass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");

        try
        {
            var settings = new NotchSettings
            {
                SettingsVersion = SettingsMigrator.CurrentVersion,
                EnableDynamicIslandMode = true,
                NotchStyle = "liquidglass"
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings));

            NotchSettings loaded = new SettingsService(path, _ => { }).Load();

            Assert.Equal("liquidglass", loaded.NotchStyle);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
