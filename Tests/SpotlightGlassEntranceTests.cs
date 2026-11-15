using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightGlassEntranceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ColdOpenAndReopenStayTranslucentWhileCaptureIsUnavailable(bool gpu, bool reduceMotion) =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool previous = AnimationConfig.ReduceMotion;
            try
            {
                AnimationConfig.SetReduceMotion(reduceMotion);
                using var fixture = new SpotlightWindowFixture(new NotchSettings
                {
                    NotchStyle = "liquidglass",
                    LiquidGlass = new LiquidGlassConfig { UseGpuRefraction = gpu }
                });
                var window = fixture.Window;
                var field = typeof(SpotlightWindow).GetField("_liquidGlass", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    window.ShowSpotlight();
                    var controller = Assert.IsType<LiquidGlassController>(field.GetValue(window));
                    // Hold capture unavailable through the queued entrance and
                    // the whole morph, including the retained-resource reopen.
                    controller.Stop(retainGpuResources: cycle == 0);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.True(window.IsSpotlightOpen);
                    Assert.False(controller.HasPresentedFrame);
                    Assert.Same(Brushes.Transparent, window.Shell.Background);
                    await WpfFrameWaiter.UntilAsync(() => double.IsNaN(window.Shell.Width), "glass entrance", ct);
                    Assert.Same(Brushes.Transparent, window.Shell.Background);
                    window.HideSpotlight();
                    await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "glass dismissal", ct);
                }
            }
            finally { AnimationConfig.SetReduceMotion(previous); }
        });

    [Fact]
    public void ShadowPixelsStayOutsideGlassAcrossMorphSizesAndSkinChanges() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
        var window = fixture.Window;
        var host = window.ShellShadowHost;
        host.Opacity = 1;
        host.Visibility = Visibility.Visible;
        host.Margin = new Thickness(0);
        host.RenderTransform = Transform.Identity;

        foreach (var (width, height, radius) in new[] { (300d, 40d, 20d), (510d, 56d, 17d), (720d, 80d, 14d) })
        {
            host.Width = width;
            host.Height = height;
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            window.GlassMaterialClipHost.Width = width;
            window.GlassMaterialClipHost.Height = height;
            window.GlassMaterialClipHost.Measure(new Size(width, height));
            window.GlassMaterialClipHost.Arrange(new Rect(0, 0, width, height));
            window.ShellTopCornerRadius = radius;
            window.ShellCornerRadius = radius;
            window.UpdateGlassClip();

            var pixels = RenderShadow(host, width, height);
            Assert.Equal(0, Alpha(pixels, width, width / 2, height / 2));
            Assert.Equal(0, Alpha(pixels, width, width / 2, height - 2));
            Assert.True(Alpha(pixels, width, width / 2, height + 4) > 0,
                $"The exterior drop shadow must remain visible at {width}x{height}.");
        }

        window.ApplySettings(new NotchSettings { NotchStyle = "default" });
        Assert.Null(host.Clip);
        var opaque = RenderShadow(host, 720, 80);
        Assert.Equal(255, Alpha(opaque, 720, 360, 40));
        window.ApplySettings(new NotchSettings { NotchStyle = "liquidglass" });
        Assert.NotNull(host.Clip);
        Assert.Equal(0, Alpha(RenderShadow(host, 720, 80), 720, 360, 40));
    });

    private const int Padding = 30;

    private static byte[] RenderShadow(FrameworkElement host, double width, double height)
    {
        int w = (int)width + Padding * 2;
        int h = (int)height + Padding * 2;
        if (VisualTreeHelper.GetParent(host) is Panel parent) parent.Children.Remove(host);
        var root = new Canvas { Width = w, Height = h };
        Canvas.SetLeft(host, Padding);
        Canvas.SetTop(host, Padding);
        root.Children.Add(host);
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, double width, double x, double y) =>
        pixels[(((int)y + Padding) * ((int)width + Padding * 2) + (int)x + Padding) * 4 + 3];
}
