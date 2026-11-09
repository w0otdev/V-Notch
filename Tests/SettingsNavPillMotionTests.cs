using System.Runtime.InteropServices;
using Xunit;

namespace VNotch.Tests;

public sealed partial class SettingsNavPillMotionTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct State
    {
        public double Top, Bottom, TopVelocity, BottomVelocity, WidthScale, WidthVelocity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Visual
    {
        public double TranslateY, ScaleX, ScaleY;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_nav_pill")]
    private static partial int Step(ref State state, double target, double height, double seconds, byte reduced,
        out Visual visual, out byte settled);

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void IndependentEdgesStretchDuringTravelAndSettleAtDifferentFrameRates(int fps)
    {
        var state = new State { Top = 1, Bottom = 31, WidthScale = 1 };
        bool stretched = false;
        byte settled = 0;
        for (int frame = 0; frame < fps * 2 && settled == 0; frame++)
        {
            Assert.Equal(0, Step(ref state, 193, 30, 1.0 / fps, 0, out var visual, out settled));
            Assert.InRange(visual.ScaleY, .72, 2.1);
            Assert.InRange(visual.ScaleX, .9, 1.035);
            Assert.True(double.IsFinite(visual.TranslateY));
            stretched |= visual.ScaleY > 1.1 && visual.ScaleX < .99;
        }
        Assert.True(stretched);
        Assert.Equal((byte)1, settled);
        Assert.Equal(193, state.Top);
        Assert.Equal(223, state.Bottom);
        Assert.Equal(0, state.TopVelocity);
        Assert.Equal(0, state.BottomVelocity);
        Assert.Equal(1, state.WidthScale);
    }

    [Fact]
    public void ReversingKeepsPositionAndMomentumInsteadOfRestartingTheSpring()
    {
        var state = new State { Top = 1, Bottom = 31, WidthScale = 1 };
        for (int frame = 0; frame < 5; frame++) Step(ref state, 193, 30, .016, 0, out _, out _);
        var before = state;
        Assert.True(before.TopVelocity > 0);
        Assert.Equal(0, Step(ref state, 1, 30, 0, 0, out _, out _));
        Assert.Equal(before.Top, state.Top);
        Assert.Equal(before.Bottom, state.Bottom);
        Assert.Equal(before.TopVelocity, state.TopVelocity);
        Assert.Equal(before.BottomVelocity, state.BottomVelocity);
        byte settled = 0;
        for (int frame = 0; frame < 120 && settled == 0; frame++)
            Step(ref state, 1, 30, .016, 0, out _, out settled);
        Assert.Equal((byte)1, settled);
        Assert.Equal(1, state.Top);
        Assert.Equal(31, state.Bottom);
    }

    [Fact]
    public void ReducedMotionAndInvalidStateResetToAFiniteRestingShape()
    {
        var state = new State { Top = double.NaN, Bottom = double.PositiveInfinity };
        Assert.Equal(0, Step(ref state, double.NaN, -1, double.NaN, 0, out var visual, out byte settled));
        Assert.Equal((byte)1, settled);
        Assert.True(double.IsFinite(visual.TranslateY));
        Assert.Equal(1, visual.ScaleX);
        Assert.Equal(1, visual.ScaleY);
        state.TopVelocity = 2000;
        Assert.Equal(0, Step(ref state, 65, 30, .1, 1, out visual, out settled));
        Assert.Equal((byte)1, settled);
        Assert.Equal(65, visual.TranslateY);
        Assert.Equal(0, state.TopVelocity);
    }
}
