using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Presenters;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class GlassMaterialClipPresenterTests
{
    [Fact]
    public void FractionalDipRoundingDoesNotRestoreBooleanClipping() => SharedStaTestRunner.Run(() =>
    {
        var size = new Size(1000.13, 500.17);
        var panel = new GlassClipGrid
        {
            Width = size.Width,
            Height = size.Height,
            ClipToBounds = true,
            Clip = GlassClipBuilder.CreateClip(size, new CornerRadius(40), 3)
        };
        panel.Measure(size); panel.Arrange(new Rect(size));
        Assert.IsType<StreamGeometry>(VisualTreeHelper.GetClip(panel));
    });

    [Fact]
    public void ExplicitSilhouetteAvoidsRepeatedBooleanGeometryAllocations() => SharedStaTestRunner.Run(() =>
    {
        Grid Arrange(Grid grid)
        {
            grid.ClipToBounds = true;
            grid.Width = 1200; grid.Height = 700;
            grid.Clip = GlassClipBuilder.CreateClip(new Size(1200, 700), new CornerRadius(40), 3);
            grid.Measure(new Size(1200, 700)); grid.Arrange(new Rect(0, 0, 1200, 700));
            return grid;
        }
        var baseline = VisualTreeHelper.GetClip(Arrange(new Grid()));
        var optimized = VisualTreeHelper.GetClip(Arrange(new GlassClipGrid()));
        Assert.IsType<CombinedGeometry>(baseline);
        Assert.IsType<StreamGeometry>(optimized);
        Assert.Equal(baseline.Bounds.X, optimized.Bounds.X, 4);
        Assert.Equal(baseline.Bounds.Y, optimized.Bounds.Y, 4);
        Assert.Equal(baseline.Bounds.Width, optimized.Bounds.Width, 4);
        Assert.Equal(baseline.Bounds.Height, optimized.Bounds.Height, 4);
        foreach (var point in new[] { new Point(1, 1), new Point(30, 30), new Point(600, 350), new Point(1199, 699) })
            Assert.Equal(baseline.FillContains(point), optimized.FillContains(point));

        long Measure(Geometry clip)
        {
            _ = clip.Bounds;
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; ++i) _ = clip.Bounds;
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        long previousBytes = Measure(baseline);
        long currentBytes = Measure(optimized);
        Console.WriteLine($"Clip bounds x100: baseline={previousBytes} bytes, optimized={currentBytes} bytes");
        Assert.True(previousBytes > 100_000);
        Assert.True(currentBytes < previousBytes / 10, $"Old={previousBytes}, new={currentBytes}");
        var resizing = Arrange(new GlassClipGrid());
        resizing.Width = 600; resizing.Height = 350;
        resizing.Measure(new Size(600, 350)); resizing.Arrange(new Rect(0, 0, 600, 350));
        Assert.False(VisualTreeHelper.GetClip(resizing).FillContains(new Point(900, 500)));
    });

    [Fact]
    public void ChangingPowerRebuildsClipAtSameSizeAndRadius() => SharedStaTestRunner.Run(() =>
    {
        var host = new Grid();
        host.Measure(new Size(300, 100));
        host.Arrange(new Rect(0, 0, 300, 100));
        var presenter = new GlassMaterialClipPresenter(host);
        presenter.UpdateClip(new CornerRadius(40));
        var circular = host.Clip;
        Assert.False(circular.FillContains(new Point(10, 10)));
        presenter.PowerFactor = 3;
        presenter.UpdateClip(new CornerRadius(40));
        Assert.NotSame(circular, host.Clip);
        Assert.True(host.Clip.FillContains(new Point(10, 10)));
    });

    [Fact]
    public void HiddenMaterialSkipsUpdatesAndSynchronizesWhenRevealed() => SharedStaTestRunner.Run(() =>
    {
        var host = new Grid { Visibility = Visibility.Collapsed };
        var backdrop = new Border();
        var rim = new Border();
        var presenter = new GlassMaterialClipPresenter(host, backdrop, rim);
        presenter.SyncCornerRadius(new CornerRadius(35));
        Assert.Equal(new CornerRadius(), backdrop.CornerRadius);
        Assert.Null(host.Clip);

        host.Visibility = Visibility.Visible;
        host.Measure(new Size(300, 100));
        host.Arrange(new Rect(0, 0, 300, 100));
        presenter.SyncCornerRadius(new CornerRadius(35));
        Assert.Equal(new CornerRadius(35), backdrop.CornerRadius);
        Assert.Equal(backdrop.CornerRadius, rim.CornerRadius);
        var clip = Assert.IsType<StreamGeometry>(host.Clip);
        Assert.Equal(new Rect(0, 0, 300, 100), clip.Bounds);
        presenter.SyncCornerRadius(new CornerRadius(35));
        Assert.Same(clip, host.Clip);

        host.Visibility = Visibility.Collapsed;
        presenter.SyncCornerRadius(new CornerRadius(15));
        Assert.Equal(new CornerRadius(35), backdrop.CornerRadius);
        Assert.Same(clip, host.Clip);

        host.Visibility = Visibility.Visible;
        presenter.SyncCornerRadius(new CornerRadius(15));
        Assert.Equal(new CornerRadius(15), backdrop.CornerRadius);
        Assert.NotSame(clip, host.Clip);
        host.Arrange(new Rect(0, 0, 200, 80));
        presenter.UpdateClip(new CornerRadius(15));
        Assert.Equal(new Rect(0, 0, 200, 80), host.Clip.Bounds);
    });
}
