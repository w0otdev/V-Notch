using System.Runtime.InteropServices;
using Xunit;

namespace VNotch.Tests;

public sealed partial class NativeMenuMotionTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Shape
    {
        public double Top, Bottom, TopVelocity, BottomVelocity, WidthScale, WidthVelocity;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Visual { public double Y, ScaleX, ScaleY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Surface
    {
        public double ScaleX, ScaleY, X, Y, Opacity;
        public double ScaleXVelocity, ScaleYVelocity, XVelocity, YVelocity, OpacityVelocity;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_menu_highlight")]
    private static partial int StepShape(ref Shape state, double target, double height, double seconds,
        byte reduced, out Visual visual, out byte settled);
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_menu_surface")]
    private static partial int StepSurface(ref Surface state, double seconds, byte reduced, out byte settled);

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void LongMenuTravelStaysCompactAndReversesWithoutResettingItsShape(int fps)
    {
        var shape = new Shape { Top = 1, Bottom = 33, WidthScale = 1 };
        for (int frame = 0; frame < 4; frame++)
        {
            Assert.Equal(0, StepShape(ref shape, 449, 32, 1.0 / fps, 0, out var visual, out _));
            Assert.InRange(visual.ScaleY, 1, 1.25);
            Assert.InRange(visual.ScaleX, .964, 1);
        }
        Assert.True(shape.TopVelocity + shape.BottomVelocity > 0);
        var before = shape;
        StepShape(ref shape, 1, 32, 0, 0, out _, out _);
        Assert.Equal(before, shape);
        byte settled = 0;
        for (int frame = 0; frame < fps * 2 && settled == 0; frame++)
        {
            StepShape(ref shape, 1, 32, 1.0 / fps, 0, out var visual, out settled);
            Assert.InRange(visual.ScaleY, 1, 1.25);
            Assert.InRange(visual.ScaleX, .964, 1);
        }
        Assert.Equal((byte)1, settled);
        Assert.Equal(1, shape.Top);
        Assert.Equal(33, shape.Bottom);
        Assert.Equal(1, shape.WidthScale);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void PopupChannelsOpenMonotonicallyAndRetainTheirMomentum(int fps)
    {
        var state = new Surface { ScaleX = .95, ScaleY = .9675, X = -6, Y = -4 };
        byte settled = 0;
        for (int frame = 0; frame < fps * 2 && settled == 0; frame++)
        {
            var before = state;
            Assert.Equal(0, StepSurface(ref state, 0, 0, out _));
            Assert.Equal(before, state);
            StepSurface(ref state, 1.0 / fps, 0, out settled);
            Assert.InRange(state.ScaleX, before.ScaleX, 1);
            Assert.InRange(state.ScaleY, before.ScaleY, 1);
            Assert.InRange(state.X, before.X, 0);
            Assert.InRange(state.Y, before.Y, 0);
            Assert.InRange(state.Opacity, before.Opacity, 1);
        }
        Assert.Equal((byte)1, settled);
        Assert.Equal(1, state.ScaleX);
        Assert.Equal(1, state.ScaleY);
        Assert.Equal(1, state.Opacity);
    }

    [Fact]
    public void ReducedMotionAndInvalidStatesRestoreFiniteRestingSurfaces()
    {
        var shape = new Shape { Top = double.NaN };
        StepShape(ref shape, 65, 32, .016, 0, out var visual, out byte settled);
        Assert.Equal((byte)1, settled);
        Assert.Equal(65, visual.Y);
        Assert.Equal(1, visual.ScaleY);
        var surface = new Surface { ScaleX = double.NaN };
        StepSurface(ref surface, .016, 0, out settled);
        Assert.Equal((byte)1, settled);
        Assert.Equal(1, surface.ScaleX);
        surface.X = -6;
        surface.XVelocity = 42;
        StepSurface(ref surface, .016, 1, out settled);
        Assert.Equal((byte)1, settled);
        Assert.Equal(0, surface.X);
        Assert.Equal(0, surface.XVelocity);
    }
}
