using System.Windows;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class GlassClipBuilderTests
{
    [Theory]
    [InlineData(1.5)]
    [InlineData(3)]
    [InlineData(4)]
    public void SquircleClipMatchesShaderBoundary(double power)
    {
        const double radius = 40;
        var geometry = GlassClipBuilder.CreateClip(new Size(200, 100), new CornerRadius(radius), power)!;
        Assert.Equal(new Rect(0, 0, 200, 100), geometry.Bounds);
        // Samples on either side of the analytical superellipse diagonal.
        double shoulder = radius - radius / Math.Pow(2, 1 / power);
        Assert.True(geometry.FillContains(new Point(shoulder + 0.5, shoulder + 0.5)));
        Assert.False(geometry.FillContains(new Point(shoulder - 0.5, shoulder - 0.5)));
        Assert.True(geometry.IsFrozen);
    }

    [Fact]
    public void RadiusChangesRebuildSilhouetteAtTheSameSize()
    {
        var size = new Size(300, 100);
        var rounded = Assert.IsType<StreamGeometry>(GlassClipBuilder.CreateClip(size, new CornerRadius(35)));
        var tighter = Assert.IsType<StreamGeometry>(GlassClipBuilder.CreateClip(size, new CornerRadius(15)));

        Assert.Equal(new Rect(size), rounded.Bounds);
        Assert.Equal(rounded.Bounds, tighter.Bounds);
        Assert.False(rounded.FillContains(new Point(5, 5)));
        Assert.True(tighter.FillContains(new Point(5, 5)));
        Assert.True(rounded.IsFrozen);
        Assert.True(tighter.IsFrozen);
    }

    [Fact]
    public void EachCornerUsesItsOwnRadius()
    {
        var geometry = Assert.IsType<StreamGeometry>(GlassClipBuilder.CreateClip(
            new Size(300, 100), new CornerRadius(0, 35, 0, 35)));

        Assert.True(geometry.FillContains(new Point(1, 1)));
        Assert.False(geometry.FillContains(new Point(299, 1)));
        Assert.True(geometry.FillContains(new Point(299, 99)));
        Assert.False(geometry.FillContains(new Point(1, 99)));
    }

    [Fact]
    public void ResizeUsesNewBoundsAndClampsOversizedRadii()
    {
        var geometry = Assert.IsType<StreamGeometry>(GlassClipBuilder.CreateClip(
            new Size(100, 40), new CornerRadius(1000)));

        Assert.Equal(new Rect(0, 0, 100, 40), geometry.Bounds);
        Assert.False(geometry.FillContains(new Point(1, 1)));
        Assert.True(geometry.FillContains(new Point(20, 1)));
        Assert.True(geometry.FillContains(new Point(50, 39)));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(300, 0)]
    [InlineData(double.NaN, 100)]
    [InlineData(300, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(300, double.PositiveInfinity)]
    public void InvalidBoundsDoNotProduceClip(double width, double height) =>
        Assert.Null(GlassClipBuilder.CreateClip(new Size(width, height), new CornerRadius(35)));

    [Fact]
    public void EmptySizeDoesNotProduceClip() =>
        Assert.Null(GlassClipBuilder.CreateClip(Size.Empty, new CornerRadius(35)));
}
