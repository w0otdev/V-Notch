using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class LiquidGlassCpuPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(96.0, false)]
    [InlineData(120.0, false)]
    [InlineData(144.0, false)]
    [InlineData(192.0, false)]
    [InlineData(96.0, true)]
    [InlineData(120.0, true)]
    [InlineData(144.0, true)]
    [InlineData(192.0, true)]
    public void CpuFramesStayAlignedInCanvasAcrossResizeAndDpi(double dpi, bool afterGpuAttempt) => SharedStaTestRunner.Run(() =>
    {
        // MainWindow and Spotlight use a Canvas so GPU captures can extend past
        // the lens. Canvas ignores HorizontalAlignment.Center for CPU bitmaps.
        var image = new Image();
        var canvas = new Canvas { Background = Brushes.Black, ClipToBounds = true };
        canvas.Children.Add(image);
        using var controller = new LiquidGlassController(image, () => IntPtr.Zero, () => null);
        Set(controller, "_isActive", true);
        Set(controller, "_bitmapDpi", dpi);
        int overscan = (int)Math.Round(80 * dpi / 96);

        // Include odd widths, reuse of pooled capacity, and a collapsed frame
        // after expansion. Stale padding is magenta and must never enter the lens.
        foreach (var (width, height) in new[] { (300, 40), (601, 184), (460, 100), (299, 40) })
        {
            if (afterGpuAttempt)
            {
                // GPU setup can change layout before its first frame arrives,
                // leaving the CPU source attached when initialization fails.
                image.Width = 1920;
                image.Height = 920;
                image.Stretch = Stretch.Fill;
                image.RenderTransform = null;
            }
            int frameWidth = width + overscan * 2;
            int frameHeight = height + overscan;
            var pixels = new byte[frameWidth * frameHeight * 4];
            for (int y = 0; y < frameHeight; y++)
            for (int x = 0; x < frameWidth; x++)
            {
                int i = (y * frameWidth + x) * 4;
                bool inside = x >= overscan && x < overscan + width && y < height;
                pixels[i] = inside ? (byte)((x - overscan) % 251) : (byte)255;
                pixels[i + 1] = inside ? (byte)(y % 251) : (byte)0;
                pixels[i + 2] = inside ? (byte)64 : (byte)255;
                pixels[i + 3] = 255;
            }
            Set(controller, "_outW", frameWidth);
            Set(controller, "_outH", frameHeight);
            Set(controller, "_mapNotchOffX", overscan);
            Set(controller, "_mapNotchOffY", 0);
            Set(controller, "_outBuffer", pixels);
            typeof(LiquidGlassController).GetMethod("Present", Private)!.Invoke(controller, [0]);
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);

            var size = new Size(width * 96.0 / dpi, height * 96.0 / dpi);
            canvas.Measure(size);
            canvas.Arrange(new Rect(size));
            canvas.UpdateLayout();
            var rendered = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            rendered.Render(canvas);
            var actual = new byte[width * height * 4];
            rendered.CopyPixels(actual, width * 4, 0);
            foreach (int y in new[] { 0, height / 2, height - 1 })
            foreach (int x in new[] { 0, 1, width / 2, width - 2, width - 1 })
            {
                int i = (y * width + x) * 4;
                Assert.InRange(actual[i], Math.Max(0, x % 251 - 1), Math.Min(255, x % 251 + 1));
                Assert.InRange(actual[i + 1], Math.Max(0, y % 251 - 1), Math.Min(255, y % 251 + 1));
                // WPF's fractional-DPI resampling can round a channel by one.
                Assert.InRange(actual[i + 2], 63, 65);
                Assert.Equal(255, actual[i + 3]);
            }
        }
        Set(controller, "_isActive", false);
    });

    private static void Set(LiquidGlassController controller, string field, object value) =>
        typeof(LiquidGlassController).GetField(field, Private)!.SetValue(controller, value);
}
