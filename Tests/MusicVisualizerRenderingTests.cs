using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class MusicVisualizerRenderingTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FramesMatchOriginalBarsAndMorphAcrossSizeColorAndDpi(double dpiScale) => SharedStaTestRunner.Run(() =>
    {
        var current = new MusicVisualizer();
        var oldVisual = new DrawingVisual();
        var newVisual = new DrawingVisual();
        var dpi = new DpiScale(dpiScale, dpiScale);
        typeof(MusicVisualizer).GetField("_cachedDpi", Instance)!.SetValue(current, dpi);
        double[] heights = (double[])typeof(MusicVisualizer).GetField("_drawHeights", Instance)!.GetValue(current)!;

        foreach (Size size in new[] { new Size(32, 18), new Size(80, 40), new Size(57.5, 31.5) })
        {
            current.Measure(size);
            current.Arrange(new Rect(size));
            foreach (Color color in new[] { Colors.White, Color.FromArgb(173, 45, 201, 132) })
            foreach (double iconMix in new[] { 0d, .04, .12, .18, .55, 1d, .08, 0d })
            foreach (double checkMix in new[] { 0d, .6, 1d })
            {
                current.ActiveBrush = new SolidColorBrush(color);
                Set(current, "_currentOpacity", .63);
                Set(current, "_iconMix", iconMix);
                Set(current, "_checkMix", checkMix);
                Set(current, "_playMix", .37);
                for (int i = 0; i < heights.Length; i++) heights[i] = .08 + ((i * .17 + iconMix * .3) % .92);
                if (current.NeedsDrawingUpdate())
                {
                    using var dc = newVisual.RenderOpen();
                    current.RenderFrame(dc);
                }
                using (var dc = oldVisual.RenderOpen()) DrawOriginal(dc, size, dpi, heights, color, .63, iconMix, checkMix, .37);
                Assert.True(Pixels(oldVisual, size, dpiScale).AsSpan().SequenceEqual(Pixels(newVisual, size, dpiScale)),
                    $"Pixels differ: dpi={dpiScale}, size={size}, color={color}, icon={iconMix}, check={checkMix}");
            }
        }
    });

    [Fact]
    public void SubpixelAmplitudeChangesReuseTheDrawingUntilAnEdgeOrOpacityMoves() => SharedStaTestRunner.Run(() =>
    {
        var current = new MusicVisualizer();
        current.Measure(new Size(80, 40));
        current.Arrange(new Rect(0, 0, 80, 40));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) current.RenderFrame(dc);
        Assert.False(current.NeedsDrawingUpdate());
        double[] heights = (double[])typeof(MusicVisualizer).GetField("_drawHeights", Instance)!.GetValue(current)!;
        heights[0] += .000001;
        Assert.False(current.NeedsDrawingUpdate());
        heights[0] = .3;
        Assert.True(current.NeedsDrawingUpdate());
        using (var dc = visual.RenderOpen()) current.RenderFrame(dc);
        Assert.False(current.NeedsDrawingUpdate());
        Set(current, "_currentOpacity", .9);
        Assert.True(current.NeedsDrawingUpdate());
        using (var dc = visual.RenderOpen()) current.RenderFrame(dc);
        current.ActiveBrush = Brushes.CornflowerBlue;
        Assert.True(current.NeedsDrawingUpdate());
    });

    private static void Set(MusicVisualizer visualizer, string field, double value) =>
        typeof(MusicVisualizer).GetField(field, Instance)!.SetValue(visualizer, value);

    // Preserve the original immediate drawing as the pixel oracle; optimizations
    // must keep every rounded corner, gradient and morph slice.
    private static void DrawOriginal(DrawingContext dc, Size size, DpiScale dpi, double[] heights,
        Color color, double opacity, double iconMix, double checkMix, double playMix)
    {
        double width = size.Width, height = size.Height;
        double barWidth = width * .10, spacing = width * .05 + .2;
        double startX = (width - (barWidth * 5 + spacing * 4)) / 2;
        double snappedWidth = Math.Max(1, Math.Round(barWidth * dpi.DpiScaleX) / dpi.DpiScaleX);
        var dark = Color.FromArgb(color.A, (byte)(color.R * .55), (byte)(color.G * .55), (byte)(color.B * .55));
        var gradient = new LinearGradientBrush(color, dark, 90) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        dc.PushOpacity(opacity + (1 - opacity) * iconMix);
        double morphOpacity = Math.Clamp(iconMix / .18, 0, 1);
        morphOpacity = morphOpacity * morphOpacity * (3 - 2 * morphOpacity);
        if (morphOpacity > 0)
        {
            var morphBrush = gradient.Clone();
            float mix = (float)Math.Clamp(iconMix, 0, 1);
            foreach (GradientStop stop in morphBrush.GradientStops)
            {
                Color c = stop.Color;
                stop.Color = Color.FromScRgb(c.ScA + (1 - c.ScA) * mix, c.ScR + (1 - c.ScR) * mix,
                    c.ScG + (1 - c.ScG) * mix, c.ScB + (1 - c.ScB) * mix);
            }
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            double iconSize = Math.Min(width, height);
            using (var path = geometry.Open())
            {
                for (int i = 0; i < 5; i++)
                {
                    Rect bounds = Bounds(i);
                    Point Corner(int corner)
                    {
                        bool right = corner is 1 or 2, bottom = corner >= 2;
                        double playX = 7 + 13d * (i + (right ? 1 : 0)) / 5;
                        double playHalfHeight = 8 * (20 - playX) / 13;
                        var play = new Point(playX, 12 + (bottom ? playHalfHeight : -playHalfHeight));
                        int slice = i < 3 ? i : i - 3, slices = i < 3 ? 3 : 2;
                        double pauseX = (i < 3 ? 6 : 14) + 4d * (slice + (right ? 1 : 0)) / slices;
                        var pause = new Point(pauseX, bottom ? 20 : 4);
                        Point icon = pause + (play - pause) * playMix;
                        bool leftArm = i < 2;
                        int checkSlice = leftArm ? i : i - 2, rightOffset = right ? 1 : 0;
                        double checkX = leftArm ? 3 + 6d * (checkSlice + rightOffset) / 2 : 9 + 12d * (checkSlice + rightOffset) / 3;
                        double checkY = leftArm ? checkX + 9 : 27 - checkX;
                        var check = new Point(checkX, checkY + (bottom ? 1.7 : -1.7));
                        icon += (check - icon) * checkMix;
                        icon = new Point((width - iconSize) / 2 + icon.X * iconSize / 24,
                            (height - iconSize) / 2 + icon.Y * iconSize / 24);
                        var bar = new Point(right ? bounds.Right : bounds.Left, bottom ? bounds.Bottom : bounds.Top);
                        return bar + (icon - bar) * iconMix;
                    }
                    path.BeginFigure(Corner(0), true, true);
                    path.LineTo(Corner(1), false, false);
                    path.LineTo(Corner(2), false, false);
                    path.LineTo(Corner(3), false, false);
                }
            }
            geometry.Freeze();
            dc.PushOpacity(morphOpacity);
            dc.DrawGeometry(morphBrush, null, geometry);
            dc.Pop();
        }
        if (morphOpacity < 1)
        {
            dc.PushOpacity(1 - morphOpacity);
            for (int i = 0; i < 5; i++) dc.DrawRoundedRectangle(gradient, null, Bounds(i), snappedWidth * .5, snappedWidth * .5);
            dc.Pop();
        }
        dc.Pop();

        Rect Bounds(int index)
        {
            double x = startX + index * (barWidth + spacing), halfHeight = heights[index] * height / 2;
            double top = Math.Round((height / 2 - halfHeight) * dpi.DpiScaleY) / dpi.DpiScaleY;
            double bottom = Math.Round((height / 2 + halfHeight) * dpi.DpiScaleY) / dpi.DpiScaleY;
            return new Rect(Math.Round(x * dpi.DpiScaleX) / dpi.DpiScaleX, top, snappedWidth, Math.Max(0, bottom - top));
        }
    }

    private static byte[] Pixels(DrawingVisual visual, Size size, double dpiScale)
    {
        int width = (int)Math.Ceiling(size.Width * dpiScale), height = (int)Math.Ceiling(size.Height * dpiScale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
