using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class AnimationConfigTests
{
    [Theory]
    [InlineData(144, 144, 144)]
    [InlineData(120, 144, 120)]
    [InlineData(120, 60, 120)]
    [InlineData(60, 144, 60)]
    [InlineData(60, 50, 60)]
    [InlineData(30, 144, 30)]
    [InlineData(45, 60, 45)]
    [InlineData(240, 240, 240)]
    [InlineData(240, 144, 240)]
    public void ComputeTargetFps_UsesConfiguredCapRegardlessOfRefreshRate(int configuredFps, int refreshHz, int expected)
    {
        Assert.Equal(expected, AnimationConfig.ComputeTargetFps(configuredFps, refreshHz));
    }

    [Fact]
    public void ComputeTargetFps_KeepsConfiguredCapWhenRefreshRateIsUnavailable()
    {
        Assert.Equal(144, AnimationConfig.ComputeTargetFps(144, null));
    }
}
