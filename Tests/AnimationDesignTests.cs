using System.Globalization;
using System.IO;
using System.Windows.Media.Animation;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AnimationDesignTests
{
    [Theory]
    [InlineData(0.32, 0.72, 0, 1)]
    [InlineData(0.4, 0, 0.2, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(1, 0, 1, 1)]
    public void EaseOutFactoryRetainsTheSuppliedCurveIncludingFlatTangents(double x1, double y1, double x2, double y2)
    {
        var easing = CubicBezierEase.FromEaseOutCurve(x1, y1, x2, y2);
        Assert.Equal(EasingMode.EaseOut, easing.EasingMode);
        double previous = 0;
        for (int i = 0; i <= 1000; i++)
        {
            double x = i / 1000d;
            // Independent high-precision bisection of the parametric curve.
            double low = 0, high = 1;
            for (int step = 0; step < 60; step++)
            {
                double t = (low + high) / 2;
                if (Bezier(t, x1, x2) < x) low = t; else high = t;
            }
            double actual = easing.Ease(x);
            Assert.InRange(Math.Abs(actual - Bezier((low + high) / 2, y1, y2)), 0, 0.000002);
            Assert.InRange(actual, previous - 0.000002, 1);
            previous = actual;
        }
    }

    [Fact]
    public void EntranceMovesQuicklyThenSettlesWithoutOvershoot()
    {
        var easing = AnimationPrimitives._easeAppleOut;
        Assert.Equal(EasingMode.EaseOut, easing.EasingMode);
        Assert.InRange(easing.Ease(.2), .6, .8);
        Assert.True(easing.Ease(.1) > 10 * (easing.Ease(1) - easing.Ease(.9)));
        Assert.Equal(0, easing.Ease(0));
        Assert.Equal(1, easing.Ease(1));
        Assert.True(easing.IsFrozen);
        Assert.Equal(EasingMode.EaseOut, AnimationPrimitives._easeAppleInOut.EasingMode);
    }

    [Fact]
    public void SpringsHaveOneSubtleOvershootAndLandExactlyOnTheTarget()
    {
        foreach (var spring in new[] { AnimationPrimitives._easeSpring, AnimationPrimitives._easeSoftSpring,
            AnimationPrimitives._easeMenuSpring, AnimationPrimitives._easeThumbSpring, AnimationPrimitives._easeHapticBounce })
        {
            Assert.Equal(.82, spring.DampingRatio);
            Assert.True(spring.IsFrozen);
            Assert.Equal(0, spring.Ease(0));
            Assert.Equal(1, spring.Ease(1));
            var samples = Enumerable.Range(0, 2001).Select(i => spring.Ease(i / 2000d)).ToArray();
            Assert.InRange(samples.Max(), 1.009, 1.012);
            Assert.All(samples, value => Assert.InRange(value, 0, 1.012));
            Assert.All(samples.Skip(1600), value => Assert.InRange(value, .999, 1.001));
            // A 200 DIP displacement peaks below 2.4 DIP. The last step is tiny.
            Assert.True((samples.Max() - 1) * 200 < 2.4);
            Assert.True(Math.Abs(spring.Ease(.999) - 1) < .00001);
            var clone = spring.Clone();
            Assert.Equal(spring.Ease(.4), ((DampedSpringEase)clone).Ease(.4));
        }
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "motion.csv"), new[] { "time,entrance,spring" }.Concat(
            Enumerable.Range(0, 1001).Select(i => string.Create(CultureInfo.InvariantCulture,
                $"{i / 1000d},{AnimationPrimitives._easeAppleOut.Ease(i / 1000d)},{AnimationPrimitives._easeSpring.Ease(i / 1000d)}"))));
    }

    private static double Bezier(double t, double p1, double p2) =>
        3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;
}
