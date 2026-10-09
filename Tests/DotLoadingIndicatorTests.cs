using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class DotLoadingIndicatorTests
{
    [Fact]
    public void MotionStopsWhenHiddenReducedOrUnloadedAndResumesWithoutResizing() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var indicator = new DotLoadingIndicator { Width = 96, Height = 96, IsActive = false };
        var surface = new Grid { Background = new SolidColorBrush(Color.FromRgb(8, 8, 8)) };
        surface.Children.Add(indicator);
        var host = new BackgroundWindow { Content = surface, Width = 160, Height = 160, Background = Brushes.Transparent };
        try
        {
            host.Show();
            await WpfFrameWaiter.UntilAsync(() => indicator.IsLoaded, "indicator loads", ct);
            if (Environment.GetEnvironmentVariable("DOT_LOADING_CAPTURE") is string capture)
            {
                var bitmap = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(capture);
                encoder.Save(file);
            }
            if (!SystemParameters.ClientAreaAnimation) return;
            var dot = Assert.IsType<Grid>(indicator.Dots.Children[0]);
            var scale = Assert.IsType<ScaleTransform>(dot.RenderTransform);
            var rotation = Assert.IsType<RotateTransform>(indicator.Dots.RenderTransform);
            var glow = Assert.IsType<System.Windows.Shapes.Ellipse>(dot.Children[0]);
            var glowScale = Assert.IsType<ScaleTransform>(glow.RenderTransform);
            Assert.Equal(24, rotation.CenterX);
            Assert.Equal(24, rotation.CenterY);
            indicator.IsActive = true;
            await WpfFrameWaiter.UntilAsync(() => indicator.IsAnimating && scale.HasAnimatedProperties, "loading motion begins", ct);
            Assert.True(rotation.HasAnimatedProperties);
            double initialAngle = rotation.Angle;
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(rotation.Angle - initialAngle) > 10, "indicator rotates around its center", ct);
            double initialGlow = glow.Opacity;
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(glow.Opacity - initialGlow) > .02 && glowScale.HasAnimatedProperties, "dot glow breathes", ct);
            Assert.True(Math.Abs(scale.ScaleX - scale.ScaleY) > .01);
            double initial = scale.ScaleX;
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(scale.ScaleX - initial) > .05, "dot size advances", ct);
            Assert.Equal(96, indicator.ActualWidth);
            Assert.Equal(96, indicator.ActualHeight);

            indicator.Visibility = Visibility.Collapsed;
            Assert.False(indicator.IsAnimating);
            await WpfFrameWaiter.UntilAsync(() => !scale.HasAnimatedProperties && !rotation.HasAnimatedProperties, "hidden indicator removes its animation clock", ct);
            Assert.Equal(0, rotation.Angle);
            indicator.Visibility = Visibility.Visible;
            await WpfFrameWaiter.UntilAsync(() => indicator.IsAnimating, "visible indicator resumes", ct);

            AnimationConfig.SetReduceMotion(true);
            Assert.False(indicator.IsAnimating);
            await WpfFrameWaiter.UntilAsync(() => !scale.HasAnimatedProperties && !rotation.HasAnimatedProperties, "reduced motion removes its animation clock", ct);
            AnimationConfig.SetReduceMotion(false);
            Assert.True(indicator.IsAnimating);

            indicator.IsActive = false;
            Assert.False(indicator.IsAnimating);
            indicator.IsActive = true;
            Assert.True(indicator.IsAnimating);
            surface.Children.Remove(indicator);
            await WpfFrameWaiter.UntilAsync(() => !indicator.IsLoaded && !indicator.IsAnimating, "unloaded indicator releases its clock", ct);
            AnimationConfig.SetReduceMotion(true);
            AnimationConfig.SetReduceMotion(false);
            Assert.False(indicator.IsAnimating);
            await WpfFrameWaiter.UntilAsync(() => !scale.HasAnimatedProperties && !rotation.HasAnimatedProperties, "unloaded clock stays removed", ct);
        }
        finally { host.Close(); AnimationConfig.SetReduceMotion(previous); }
    });
}
