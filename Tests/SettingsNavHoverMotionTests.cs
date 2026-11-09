using System.Runtime.InteropServices;
using Xunit;

namespace VNotch.Tests;

public sealed partial class SettingsNavHoverMotionTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct State
    {
        public double Y, YVelocity, Opacity, OpacityVelocity, SelectedOpacity, SelectedVelocity;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_nav_hover")]
    private static partial int Step(ref State state, double target, byte visible, byte selected,
        double seconds, byte reduced, out byte settled);

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void ReversingPreservesPositionAndVelocityAndEventuallySettles(int fps)
    {
        var state = new State { Y = 33, Opacity = 1 };
        for (int frame = 0; frame < 4; frame++) Step(ref state, 225, 1, 0, 1.0 / fps, 0, out _);
        Assert.True(state.YVelocity > 0);
        var before = state;
        Assert.Equal(0, Step(ref state, 65, 1, 0, 0, 0, out _));
        Assert.Equal(before, state);
        byte settled = 0;
        for (int frame = 0; frame < fps * 2 && settled == 0; frame++)
        {
            Assert.Equal(0, Step(ref state, 65, 1, 0, 1.0 / fps, 0, out settled));
            Assert.True(double.IsFinite(state.Y));
            Assert.Equal(1, state.Opacity);
        }
        Assert.Equal((byte)1, settled);
        Assert.Equal(65, state.Y);
        Assert.Equal(0, state.YVelocity);
    }

    [Fact]
    public void ReenteringDuringFadeOutPreservesOpacityAndItsVelocity()
    {
        var state = new State { Y = 65, Opacity = 1 };
        Step(ref state, 65, 0, 0, .03, 0, out _);
        Assert.InRange(state.Opacity, .1, .99);
        Assert.True(state.OpacityVelocity < 0);
        var before = state;
        Step(ref state, 129, 1, 0, 0, 0, out _);
        Assert.Equal(before, state);
        for (int frame = 0; frame < 90; frame++) Step(ref state, 129, 1, 0, 1.0 / 144, 0, out _);
        Assert.Equal(129, state.Y);
        Assert.Equal(1, state.Opacity);
        Step(ref state, 129, 0, 1, .1, 1, out byte settled);
        Assert.Equal((byte)1, settled);
        Assert.Equal(0, state.Opacity);
        Assert.Equal(1, state.SelectedOpacity);
        Assert.Equal(0, state.OpacityVelocity);
    }
}
