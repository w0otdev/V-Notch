using System.Windows;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightLayoutMetricsTests
{
    [Theory]
    [InlineData(800, 700, 800)]
    [InlineData(0, 700, 700)]
    [InlineData(double.NaN, 700, 700)]
    [InlineData(double.PositiveInfinity, 700, 700)]
    [InlineData(0, double.NaN, 768)]
    [InlineData(-1, 0, 768)]
    public void MeasureWidthUsesAUsableLayoutWidth(double actual, double configured, double expected) =>
        Assert.Equal(expected, SpotlightLayoutMetrics.ResolveMeasureWidth(actual, configured));

    [Fact]
    public void EntranceExcludesShakeSpaceAndVerticalMargins()
    {
        Assert.Equal(new Size(700, 320), SpotlightLayoutMetrics.CalculateEntranceSize(
            768, 352, 30, new Thickness(34, 12, 34, 20)));
    }

    [Theory]
    [InlineData(double.NaN, 40, 40)]
    [InlineData(double.PositiveInfinity, 40, 40)]
    [InlineData(0, 40, 40)]
    [InlineData(5, 0, 1)]
    [InlineData(5, double.NaN, 1)]
    public void InvalidDesiredHeightFallsBackWithoutProducingANegativeSize(double desired, double actual, double expected)
    {
        var size = SpotlightLayoutMetrics.CalculateEntranceSize(10, desired, actual, new Thickness(20));
        Assert.Equal(1, size.Width);
        Assert.Equal(expected, size.Height);
    }
}
