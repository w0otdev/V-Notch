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

    // Solving's regular preset is also used at 40px by the Details.so demo.
    // Compare against the independently published 64px golden vectors, including
    // a middle-depth point in the active band (not just the sphere's silhouette).
    [Theory]
    [InlineData(.6, 32.999536, 35.206761, -.991773, .3, .617779,
        9.978009, 17.732940, -.005458, .725287, .211474)]
    [InlineData(1.7, 36.224701, 34.103952, -.983692, .3, .615597,
        10.294203, 46.708964, .038909, .616292, .339495)]
    [InlineData(3.3, 31.549989, 36.092028, -.987617, .3, .616656,
        34.169914, 58.140182, -.027480, .592843, .357420)]
    [InlineData(5.1, 34.153811, 31.852573, -.996610, .3, .619085,
        29.908102, 5.879939, -.052585, .583975, .364198)]
    public void SolvingMatchesUpstreamGoldenVectors(double time,
        double x, double y, double z, double radius, double ink,
        double middleX, double middleY, double middleZ, double middleRadius, double middleInk)
    {
        var dots = new ThinkingOrbGeometry.Dot[ThinkingOrbGeometry.Capacity];
        int count = ThinkingOrbGeometry.WriteFrame(dots, ThinkingOrbState.Solving, 64, time / 1.82);
        Assert.Equal(138, count);
        AssertDot(dots[0], [x, y, z, radius, ink, 1]);
        AssertDot(dots[68], [middleX, middleY, middleZ, middleRadius, middleInk, 1]);
        for (int i = 1; i < count; i++) Assert.True(dots[i].Z >= dots[i - 1].Z);
    }

    private static void AssertDot(ThinkingOrbGeometry.Dot actual, double[] expected)
    {
        double[] values = [actual.X, actual.Y, actual.Z, actual.Radius, actual.Ink, actual.Alpha];
        for (int i = 0; i < values.Length; i++) Assert.InRange(values[i], expected[i] - .0001, expected[i] + .0001);
    }
}
