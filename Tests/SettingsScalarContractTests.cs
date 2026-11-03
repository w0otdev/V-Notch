using System.Reflection;
using System.Runtime.InteropServices;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed partial class SettingsScalarContractTests
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_settings_clamp_int(int value, int min, int max, out int result);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_settings_clamp_double(double value, double min, double max, double fallback, out double result);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_settings_double_equals(double left, double right, byte allowNan, out byte equal);

    private static T Helper<T>(string name) where T : Delegate => typeof(NotchSettings)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<T>();

    [Fact]
    public void IntegerClampRetainsNativeBoundsAndFallbackBehavior()
    {
        var clamp = Helper<Func<int, int, int, int>>("ClampInt");
        foreach (int value in new[] { int.MinValue, -1, 0, 1, 100, int.MaxValue })
            foreach (var (min, max) in new[] { (0, 100), (32, 512), (0, 0), (1, 0), (int.MinValue, int.MaxValue) })
            {
                int status = vn_settings_clamp_int(value, min, max, out int expected);
                Assert.Equal(status == 0 ? expected : value, clamp(value, min, max));
            }
    }

    [Fact]
    public void DoubleClampRetainsNonfiniteAndInvalidRangeFallbacks()
    {
        var clamp = Helper<Func<double, double, double, double, double>>("ClampDouble");
        foreach (double value in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1d, 0d, .55, 1d, 2d })
            foreach (var (min, max) in new[] { (0d, 1d), (1d, 0d), (double.NaN, 1d), (0d, double.PositiveInfinity), (0d, 0d) })
            {
                int status = vn_settings_clamp_double(value, min, max, .55, out double expected);
                Assert.Equal(status == 0 ? expected : .55, clamp(value, min, max, .55));
            }
    }

    [Fact]
    public void SettingsEqualityRetainsNativeToleranceNaNAndInfinityRules()
    {
        var equals = Helper<Func<double, double, bool>>("DoubleEquals");
        double[] values = [double.NaN, double.NegativeInfinity, double.PositiveInfinity,
            double.MinValue, -0d, 0d, .0001, .00010001, 1d, 1.0001, double.MaxValue];
        foreach (double left in values)
            foreach (double right in values)
            {
                Assert.Equal(0, vn_settings_double_equals(left, right, 0, out byte expected));
                Assert.Equal(expected != 0, equals(left, right));
                var a = new NotchSettings { Opacity = left };
                var b = a.Clone();
                b.Opacity = right;
                Assert.Equal(expected != 0, a.ValueEquals(b));
            }
    }
}
