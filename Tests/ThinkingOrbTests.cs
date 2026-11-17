using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class ThinkingOrbTests
{
    // First/last particles from upstream spec/orbs-golden.json (MIT, v0.3.1).
    // Upstream vectors use scaled engine time; the control accepts elapsed seconds.
    [Theory]
    [InlineData(ThinkingOrbState.Working, .6 / 3.9, 39,
        11.235898, 9.940794, -7.451958, .425401, .72, .202026,
        9.230714, 10.206142, 7.511880, 1.321364, .080613, 1)]
    [InlineData(ThinkingOrbState.Working, 3.3 / 3.9, 39,
        9.799297, 10.124795, -7.550283, .567387, .299946, 1,
        11.100429, 10.065749, 7.473109, .425401, .72, .498394)]
    [InlineData(ThinkingOrbState.Composing, .6 / 3.12, 208,
        7.795005, 12.112063, -7.177547, .3, .682444, .423940,
        10, 6.759833, 7.095162, .431603, .279880, .972891)]
    [InlineData(ThinkingOrbState.Composing, 3.3 / 3.12, 208,
        12.254990, 12.510684, -7.032175, .3, .678343, .429532,
        10, 7.503918, 7.389829, .436692, .271569, .984224)]
    public void NativeGeometryMatchesUpstreamGoldenVectors(ThinkingOrbState state, double seconds, int expectedCount,
        double x, double y, double z, double radius, double ink, double alpha,
        double lastX, double lastY, double lastZ, double lastRadius, double lastInk, double lastAlpha)
    {
        var dots = new ThinkingOrbGeometry.Dot[ThinkingOrbGeometry.Capacity];
        int count = ThinkingOrbGeometry.WriteFrame(dots, state, 20, seconds);
        Assert.Equal(expectedCount, count);
        AssertDot(dots[0], [x, y, z, radius, ink, alpha]);
        AssertDot(dots[count - 1], [lastX, lastY, lastZ, lastRadius, lastInk, lastAlpha]);
        for (int i = 0; i < count; i++)
        {
            Assert.InRange(dots[i].X, 0, 20);
            Assert.InRange(dots[i].Y, 0, 20);
            Assert.InRange(dots[i].Radius, .3, 2);
            if (i > 0) Assert.True(dots[i].Z >= dots[i - 1].Z);
        }
    }

    // Distances in 3D distinguish a layer turn from whole-sphere rotation,
    // and expose dots bunching together even when the silhouette stays round.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SolvingTurnsEachSideWithoutCrowding(int turn)
    {
        double[] initialDistances = PairDistances(SolveFrame(0), 152, 34);
        for (int sample = 1; sample < 10; sample++)
        {
            var dots = SolveFrame(turn * 2.2 + 1.65 * sample / 10);
            double[] distances = PairDistances(dots, dots.Length, 34);
            Assert.True(distances[0] >= .19 * .19, "Moving and fixed rows must remain separated.");
            if (sample == 5)
                Assert.True(distances.Zip(initialDistances, (a, b) => Math.Abs(a - b)).Max() > .005,
                    "The layer must turn independently of the rest of the orb.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(60)]
    public void SolvingRealignsAndSettlesAfterEveryQuarterTurn(int turn)
    {
        double[] initialDistances = PairDistances(SolveFrame(0), 152, 34);
        foreach (double offset in new[] { 1.65, 1.9, 2.2 })
        {
            double[] distances = PairDistances(SolveFrame(turn * 2.2 + offset), 152, 34);
            for (int i = 0; i < initialDistances.Length; i++)
                Assert.InRange(Math.Abs(distances[i] - initialDistances[i]), 0, 1e-10);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SolvingDoesNotJumpWhenChangingSidesOrRepeating(int turn)
    {
        var before = SolveFrame(turn * 2.2 - 1e-5);
        var after = SolveFrame(turn * 2.2 + 1e-5);
        foreach (var dot in before)
        {
            var nearest = after.MinBy(candidate => DistanceSquared(dot, candidate, 34));
            Assert.InRange(DistanceSquared(dot, nearest, 34), 0, 1e-8);
            Assert.InRange(Math.Abs(dot.Radius - nearest.Radius), 0, 1e-4);
            Assert.InRange(Math.Abs(dot.Ink - nearest.Ink), 0, 1e-4);
        }
    }

    private static ThinkingOrbGeometry.Dot[] SolveFrame(double time)
    {
        const double size = 34, dotScale = 1.6;
        var dots = new ThinkingOrbGeometry.Dot[ThinkingOrbGeometry.Capacity];
        int count = ThinkingOrbGeometry.WriteFrame(dots, ThinkingOrbState.Solving, size, time / 7.28);
        Assert.Equal(152, count);
        for (int i = 0; i < count; i++)
        {
            var dot = dots[i];
            double x = (dot.X - size / 2) / (size * .41);
            double y = (dot.Y - size / 2) / (size * .41);
            Assert.InRange(x * x + y * y + dot.Z * dot.Z, 1 - 1e-10, 1 + 1e-10);
            double radius = dot.Radius * dotScale;
            Assert.InRange(dot.X, radius, size - radius);
            Assert.InRange(dot.Y, radius, size - radius);
            Assert.InRange(dot.Ink, 0, 1);
            Assert.Equal(1, dot.Alpha);
            if (i > 0) Assert.True(dot.Z >= dots[i - 1].Z);
        }
        return dots[..count];
    }

    private static double[] PairDistances(ThinkingOrbGeometry.Dot[] dots, int count, double size)
    {
        var distances = new double[count * (count - 1) / 2];
        int index = 0;
        for (int i = 0; i < count; i++)
        for (int j = i + 1; j < count; j++)
            distances[index++] = DistanceSquared(dots[i], dots[j], size);
        Array.Sort(distances);
        return distances;
    }

    private static double DistanceSquared(ThinkingOrbGeometry.Dot a, ThinkingOrbGeometry.Dot b, double size)
    {
        double x = (a.X - b.X) / (size * .41);
        double y = (a.Y - b.Y) / (size * .41);
        double z = a.Z - b.Z;
        return x * x + y * y + z * z;
    }

    private static void AssertDot(ThinkingOrbGeometry.Dot actual, double[] expected)
    {
        double[] values = [actual.X, actual.Y, actual.Z, actual.Radius, actual.Ink, actual.Alpha];
        for (int i = 0; i < values.Length; i++) Assert.InRange(values[i], expected[i] - .0001, expected[i] + .0001);
    }
}
