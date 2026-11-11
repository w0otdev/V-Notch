using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed partial class SpringEasingPerformanceTests(ITestOutputHelper output)
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_damped_spring_build_segments(double dampingRatio, Span<double> coefficients, int count);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDampingIsRejectedBeforeSampling(double damping)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DampedSpringEase(damping));
        double[] coefficients = [42, 43, 44, 45];
        Assert.Equal(-1, vn_damped_spring_build_segments(damping, coefficients, coefficients.Length));
        Assert.Equal([42d, 43d, 44d, 45d], coefficients);
    }

    [Fact]
    public void SegmentBuilderValidatesCapacityAndPreservesNodeValues()
    {
        double[] coefficients = Enumerable.Repeat(42d, 9).ToArray();
        Assert.Equal(-1, vn_damped_spring_build_segments(.82, coefficients, 0));
        Assert.Equal(-1, vn_damped_spring_build_segments(.82, coefficients, 5));
        Assert.All(coefficients, value => Assert.Equal(42, value));
        Assert.Equal(0, vn_damped_spring_build_segments(.82, coefficients, 8));
        Assert.Equal(42, coefficients[8]);
        Assert.Equal(0, coefficients[0]);
        double midpoint = AnalyticSpringCore(.82, .5);
        Assert.Equal(midpoint, coefficients[4], 12);
        Assert.Equal(1, coefficients.Skip(4).Take(4).Sum(), 12);
    }

    [Fact]
    public void BuiltInSpringsShareAtMostSixteenKiBOfCurveStorage()
    {
        var storage = typeof(DampedSpringEase).GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bounce = (double[])storage.GetValue(new DampedSpringEase(.82))!;
        var move = (double[])storage.GetValue(new DampedSpringEase(1))!;
        Assert.Same(bounce, storage.GetValue(new DampedSpringEase(.82)));
        Assert.Same(move, storage.GetValue(new DampedSpringEase(1)));
        Assert.InRange((bounce.Length + move.Length) * sizeof(double), 1, 16 * 1024);
        output.WriteLine($"Shared spring coefficient payload: {(bounce.Length + move.Length) * sizeof(double)} bytes; previous sample tables: {2 * 8193 * sizeof(double)} bytes.");
    }

    [Theory]
    [InlineData(.82)]
    [InlineData(1)]
    [InlineData(.001)]
    [InlineData(.5)]
    [InlineData(.999999)]
    public void EasingMatchesNativeCurveWithoutSteadyStateAllocations(double damping)
    {
        var easing = new DampedSpringEase(damping);
        easing.Freeze();
        var baseline = new ScalarSpringEase(damping);
        baseline.Freeze();
        double maxError = 0;
        for (int i = 0; i <= 100_000; i++)
        {
            double time = i / 100_000d;
            maxError = Math.Max(maxError, Math.Abs(baseline.Ease(time) - easing.Ease(time)));
        }
        Assert.InRange(maxError, 0, .000002);
        const int iterations = 100_000;
        for (int i = 0; i < 3; i++) { Measure(easing, iterations); Measure(baseline, iterations); }
        var current = new double[21];
        var previous = new double[21];
        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = Measure(easing, iterations).Sum;
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128);
        for (int sample = 0; sample < current.Length; sample++)
        {
            if ((sample & 1) == 0)
            {
                previous[sample] = Measure(baseline, iterations).Nanoseconds;
                current[sample] = Measure(easing, iterations).Nanoseconds;
            }
            else
            {
                current[sample] = Measure(easing, iterations).Nanoseconds;
                previous[sample] = Measure(baseline, iterations).Nanoseconds;
            }
        }
        Array.Sort(current);
        Array.Sort(previous);
        output.WriteLine($"damping={damping}: analytic={previous[10]:F1} ns/ease, current={current[10]:F1} ns/ease, ratio={previous[10] / current[10]:F2}x, max error={maxError:G6}, checksum={sum:F3}");
    }

    private static (double Nanoseconds, double Sum) Measure(IEasingFunction easing, int iterations)
    {
        double sum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) sum += easing.Ease((i % 1001) / 1000d);
        return (Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations, sum);
    }

    private static double StepResponse(double dampingRatio, double dampedFrequency, double t)
    {
        const double naturalFrequency = 12.0;
        if (dampingRatio >= 1.0 || dampedFrequency <= 1e-9)
            return 1.0 - Math.Exp(-naturalFrequency * t) * (1.0 + naturalFrequency * t);
        double decay = Math.Exp(-dampingRatio * naturalFrequency * t);
        double phase = dampedFrequency * t;
        return 1.0 - decay * (Math.Cos(phase) + dampingRatio * naturalFrequency / dampedFrequency * Math.Sin(phase));
    }

    private static double AnalyticSpringCore(double dampingRatio, double normalizedTime)
    {
        const double naturalFrequency = 12.0;
        if (!double.IsFinite(normalizedTime)) return normalizedTime > 0.0 ? 1.0 : 0.0;
        double time = Math.Clamp(normalizedTime, 0.0, 1.0);
        if (time == 0.0 || time == 1.0) return time;
        double dampedFrequency = naturalFrequency * Math.Sqrt(Math.Max(0.0, 1.0 - dampingRatio * dampingRatio));
        double endResponse = StepResponse(dampingRatio, dampedFrequency, 1.0);
        return 1.0 - StepResponse(dampingRatio, dampedFrequency, 1.0 - time) / endResponse;
    }

    // Analytic per-frame reference oracle.
    private sealed class ScalarSpringEase(double damping) : EasingFunctionBase
    {
        protected override double EaseInCore(double time) =>
            double.IsFinite(damping) && damping > 0.0 && damping <= 1.0
                ? AnalyticSpringCore(damping, time)
                : Math.Clamp(time, 0, 1);
        protected override Freezable CreateInstanceCore() => new ScalarSpringEase(damping);
    }
}
