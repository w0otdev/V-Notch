using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Benchmarks;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class AnalogClockRenderingTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void RetainedTicksMatchOriginalPixelsAcrossResizeDateAndMidnight(double dpi)
    {
        SharedStaTestRunner.Run(() =>
        {
            if (Application.Current == null)
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
                application.Resources["SFProText"] = new FontFamily("Segoe UI");
                application.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
            }
            var baseline = new BaselineAnalogClock();
            var current = new AnalogClock();
            var oldVisual = new DrawingVisual();
            var newVisual = new DrawingVisual();
            var time = new DateTime(2026, 9, 19, 23, 59, 59, 900);
            Assert.True(current.UpdateFrame(time));
            foreach (bool showDate in new[] { true, false, true })
            foreach (double size in new[] { 92.0, 117.5, 184.0, 92.0 })
            {
                baseline.ShowDate = current.ShowDate = showDate;
                foreach (FrameworkElement clock in new FrameworkElement[] { baseline, current })
                {
                    clock.Measure(new Size(size, size));
                    clock.Arrange(new Rect(0, 0, size, size));
                }
                using (var dc = newVisual.RenderOpen()) current.RenderFrame(dc, time);
                for (int frame = 0; frame < 8; frame++)
                {
                    time = time.AddMilliseconds(33);
                    using (var dc = oldVisual.RenderOpen()) baseline.RenderFrame(dc, time);
                    if (current.UpdateFrame(time))
                    {
                        using var dc = newVisual.RenderOpen();
                        current.RenderFrame(dc, time);
                    }
                    Assert.True(Pixels(oldVisual, size, dpi).AsSpan().SequenceEqual(Pixels(newVisual, size, dpi)),
                        $"Pixels differ: dpi={dpi}, size={size}, date={showDate}, time={time:O}");
                    Assert.False(current.UpdateFrame(time), "An ordinary tick must not request a new drawing");
                }
            }
            current.ShowDate = !current.ShowDate;
            Assert.True(current.UpdateFrame(time), "Changing date visibility requires a face redraw");
            using (var dc = newVisual.RenderOpen()) current.RenderFrame(dc, time);
            typeof(AnalogClock).GetMethod("OnDpiChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(current, [new DpiScale(1, 1), new DpiScale(1.5, 1.5)]);
            Assert.True(current.UpdateFrame(time), "Moving between DPIs requires rebuilding the face");
        });
    }

    private static byte[] Pixels(DrawingVisual visual, double size, double dpi)
    {
        int width = (int)Math.Ceiling(size * dpi / 96);
        var bitmap = new RenderTargetBitmap(width, width, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[width * width * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
