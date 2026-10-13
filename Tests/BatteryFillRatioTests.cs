using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class BatteryFillRatioTests
{
    [Theory]
    [InlineData(-1, true, 0)]
    [InlineData(0, true, 0)]
    [InlineData(1, true, 0.01)]
    [InlineData(50, true, 0.5)]
    [InlineData(100, true, 1)]
    [InlineData(150, true, 1)]
    [InlineData(100, false, 0)]
    public void FillIsBoundedForUnknownEmptyFullAndMissingBatteries(int percentage, bool hasBattery, double expected)
    {
        var battery = new BatteryInfo { Percentage = percentage, HasBattery = hasBattery };
        Assert.Equal(expected, battery.FillRatio);
    }
}
