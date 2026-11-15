using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class GlassContourBorderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ReadabilityOverlayFillsAllFourSquircleCornersAtDisplayScale(double scale) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
        var overlay = fixture.Window.GlassDarkOverlay;
        overlay.Opacity = 1;
        overlay.Visibility = Visibility.Visible;
        overlay.CornerRadius = new CornerRadius(20);
        var image = Render(overlay, scale);
        Assert.True(Alpha(image, 40, 30, scale) == 255,
            $"Overlay center: opacity={overlay.Opacity}, background={overlay.Background}, size={overlay.RenderSize}, power={overlay.PowerFactor}, clip={VisualTreeHelper.GetClip(overlay)}");
        // These are inside the p=3 glass contour but outside a circular p=2
        // border. The readability tint must not leave a bright crescent here.
        foreach (var (x, y) in new[] { (5d, 5d), (74d, 5d), (5d, 54d), (74d, 54d) })
            Assert.True(Alpha(image, x, y, scale) >= 245, $"Corner {x},{y}: alpha={Alpha(image, x, y, scale)}, radius={overlay.CornerRadius}, power={overlay.PowerFactor}");
        Assert.Equal(0, Alpha(image, 0, 0, scale));
        Assert.Equal(255, Alpha(image, 40, 30, scale));
    });

    [Fact]
    public void ChangingGlassPowerUpdatesEveryLayerAndTheShadow() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
        var window = fixture.Window;
        foreach (double power in new[] { 2d, 4d, 3d })
        {
            window.ApplySettings(new NotchSettings
            {
                NotchStyle = "liquidglass",
                LiquidGlass = new LiquidGlassConfig { PowerFactor = power }
            });
            foreach (var layer in window.GlassMaterialClipHost.Children.OfType<GlassContourBorder>())
                Assert.Equal(power, layer.PowerFactor);
            Assert.Equal(power, window.ShellShadowBorder.PowerFactor);
        }
    });

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void OpticalRimLeavesTheInteriorAndExteriorTransparent(double power) => SharedStaTestRunner.Run(() =>
    {
        var rim = new GlassContourBorder
        {
            CornerRadius = new CornerRadius(20), PowerFactor = power,
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1)
        };
        var image = Render(rim, 2);
        Assert.Equal(0, Alpha(image, 40, 30, 2));
        Assert.Equal(0, Alpha(image, 0, 0, 2));
        Assert.InRange(Alpha(image, 40, 0, 2), (byte)245, byte.MaxValue);
        Assert.Equal(0, Alpha(image, 40, 2, 2));
    });

    private static BitmapSource Render(FrameworkElement element, double scale)
    {
        if (VisualTreeHelper.GetParent(element) is Panel parent) parent.Children.Remove(element);
        element.Width = 80;
        element.Height = 60;
        var root = new Canvas { Width = 80, Height = 60 };
        root.Children.Add(element);
        root.Measure(new Size(80, 60));
        root.Arrange(new Rect(0, 0, 80, 60));
        var bitmap = new RenderTargetBitmap((int)(80 * scale), (int)(60 * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    private static byte Alpha(BitmapSource image, double x, double y, double scale)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect((int)(x * scale), (int)(y * scale), 1, 1), pixel, 4, 0);
        return pixel[3];
    }
}
