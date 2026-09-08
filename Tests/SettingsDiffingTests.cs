using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsDiffingTests
{
    [Fact]
    public void LiquidGlassConfig_ValueEquals_IdenticalInstances_ReturnsTrue()
    {
        var a = new LiquidGlassConfig();
        var b = a.Clone();

        Assert.True(a.ValueEquals(b));
        Assert.True(b.ValueEquals(a));
        Assert.True(a.ValueEquals(a));
    }

    [Fact]
    public void LiquidGlassConfig_ValueEquals_NullComparison_ReturnsFalse()
    {
        var a = new LiquidGlassConfig();

        Assert.False(a.ValueEquals(null));
    }

    [Fact]
    public void LiquidGlassConfig_ValueEquals_ModifiedProperty_ReturnsFalse()
    {
        var a = new LiquidGlassConfig { BlurAmount = 0.3 };
        var b = a.Clone();

        Assert.True(a.ValueEquals(b));

        b.BlurAmount = 0.5;
        Assert.False(a.ValueEquals(b));

        b.BlurAmount = a.BlurAmount;
        Assert.True(a.ValueEquals(b));

        b.Refraction = 2.5;
        Assert.False(a.ValueEquals(b));

        b.Refraction = a.Refraction;
        b.UseGpuRefraction = !a.UseGpuRefraction;
        Assert.False(a.ValueEquals(b));
    }

    [Fact]
    public void NotchSettings_Clone_ProducesEqualLiquidGlassValues()
    {
        var settings = new NotchSettings
        {
            AnimationFps = 144,
            Width = 300,
            Height = 40,
            LiquidGlass = new LiquidGlassConfig { BlurAmount = 0.7, Refraction = 1.2 }
        };

        var clone = settings.Clone();

        Assert.Equal(settings.AnimationFps, clone.AnimationFps);
        Assert.Equal(settings.Width, clone.Width);
        Assert.True(settings.LiquidGlass.ValueEquals(clone.LiquidGlass));

        clone.LiquidGlass.BlurAmount = 0.9;
        Assert.False(settings.LiquidGlass.ValueEquals(clone.LiquidGlass));
    }
}
