using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ChangelogBlurWaveTests
{
    [Theory]
    [InlineData(Orientation.Horizontal, true, 1.0)]
    [InlineData(Orientation.Horizontal, false, 1.0)]
    [InlineData(Orientation.Vertical, true, 1.0)]
    [InlineData(Orientation.Vertical, false, 1.0)]
    [InlineData(Orientation.Horizontal, true, 1.25)]
    [InlineData(Orientation.Horizontal, false, 1.25)]
    [InlineData(Orientation.Vertical, true, 1.25)]
    [InlineData(Orientation.Vertical, false, 1.25)]
    public void OnlyTheMovingBandSoftensTheOutgoingPage(Orientation axis, bool forward, double dpiScale) => SharedStaTestRunner.Run(() =>
    {
        bool oldMotion = AnimationConfig.ReduceMotion;
        var oldSystem = AnimationConfig.ClientAreaAnimationOverride;
        const int length = 280;
        var host = new Grid { Width = length, Height = length };
        var page = new Border();
        var wave = new ChangelogBlurWave();
        host.Children.Add(page);
        host.Children.Add(wave);
        var window = new BackgroundWindow { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            var pattern = new DrawingGroup();
            using (var dc = pattern.Open())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, length, length));
                for (int i = 0; i < length; i += 4)
                    dc.DrawRectangle(Brushes.White, null, axis == Orientation.Horizontal
                        ? new Rect(i, 0, 2, length) : new Rect(0, i, length, 2));
            }
            page.Background = new DrawingBrush(pattern);
            window.Show();
            VisualTreeHelper.SetRootDpi(window, new DpiScale(dpiScale, dpiScale));
            window.UpdateLayout();
            wave.Capture(host, axis, forward);
            page.Background = Brushes.Blue;
            window.UpdateLayout();
            var clock = (AnimationClock)typeof(ChangelogBlurWave)
                .GetField("_clock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wave)!;
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(axis == Orientation.Horizontal ? 200 : 175), TimeSeekOrigin.BeginTime);
            double progress = wave.Progress;
            Assert.InRange(progress, .22, .80);
            var bitmap = new RenderTargetBitmap(length, length, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[length * length * 4];
            bitmap.CopyPixels(pixels, length * 4, 0);
            int Red(int coord) => pixels[Pixel(coord) + 2];
            int Pixel(int coord)
            {
                int p = forward ? coord : length - 1 - coord;
                return (axis == Orientation.Horizontal ? length * (length / 2) + p : length * p + length / 2) * 4;
            }
            Assert.Equal(255, pixels[Pixel(0)]);
            Assert.Equal(0, Red(0));
            Assert.True(Math.Abs(Red(length - 1) - Red(length - 3)) > 180, "Untouched text should remain sharp.");
            int softCore = (int)Math.Round((length + 84) * progress - 84 * .54);
            for (int i = softCore - 2; i <= softCore + 2; i++)
                Assert.InRange(Red(i), 48, 210);
            wave.Capture(host, axis, !forward);
            wave.Capture(host, axis, forward);
            Assert.False(wave.IsVisible);
        }
        finally
        {
            window.Close();
            AnimationConfig.ClientAreaAnimationOverride = oldSystem;
            AnimationConfig.SetReduceMotion(oldMotion);
        }
    });
}
