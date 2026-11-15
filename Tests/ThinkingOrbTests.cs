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

    private static void AssertDot(ThinkingOrbGeometry.Dot actual, double[] expected)
    {
        double[] values = [actual.X, actual.Y, actual.Z, actual.Radius, actual.Ink, actual.Alpha];
        for (int i = 0; i < values.Length; i++) Assert.InRange(values[i], expected[i] - .0001, expected[i] + .0001);
    }
}
