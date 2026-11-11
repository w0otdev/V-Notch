using System.Reflection;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsScalarContractTests
{
    private static T Helper<T>(string name) where T : Delegate => typeof(NotchSettings)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<T>();

    [Fact]
    public void IntegerClampRetainsBoundsAndFallbackBehavior()
    {
        var clamp = Helper<Func<int, int, int, int>>("ClampInt");
        foreach (int value in new[] { int.MinValue, -1, 0, 1, 100, int.MaxValue })
            foreach (var (min, max) in new[] { (0, 100), (32, 512), (0, 0), (1, 0), (int.MinValue, int.MaxValue) })
            {
                int expected = min <= max ? Math.Clamp(value, min, max) : value;
                Assert.Equal(expected, clamp(value, min, max));
            }
    }

    [Fact]
    public void DoubleClampRetainsNonfiniteAndInvalidRangeFallbacks()
    {
        var clamp = Helper<Func<double, double, double, double, double>>("ClampDouble");
        foreach (double value in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1d, 0d, .55, 1d, 2d })
            foreach (var (min, max) in new[] { (0d, 1d), (1d, 0d), (double.NaN, 1d), (0d, double.PositiveInfinity), (0d, 0d) })
            {
                double expected = double.IsFinite(min) && double.IsFinite(max) && min <= max
                    ? (double.IsFinite(value) ? Math.Clamp(value, min, max) : .55)
                    : .55;
                Assert.Equal(expected, clamp(value, min, max, .55));
            }
    }

    [Fact]
    public void SettingsEqualityRetainsToleranceNaNAndInfinityRules()
    {
        var equals = Helper<Func<double, double, bool>>("DoubleEquals");
        double[] values = [double.NaN, double.NegativeInfinity, double.PositiveInfinity,
            double.MinValue, -0d, 0d, .0001, .00010001, 1d, 1.0001, double.MaxValue];
        foreach (double left in values)
            foreach (double right in values)
            {
                bool expected = double.IsFinite(left) && double.IsFinite(right)
                    ? Math.Abs(left - right) <= 0.0001
                    : double.IsInfinity(left) && double.IsInfinity(right) && (left > 0.0) == (right > 0.0);
                Assert.Equal(expected, equals(left, right));
                var a = new NotchSettings { Opacity = left };
                var b = a.Clone();
                b.Opacity = right;
                Assert.Equal(expected, a.ValueEquals(b));
            }
    }
}
