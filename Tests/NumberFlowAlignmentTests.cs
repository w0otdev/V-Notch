using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NumberFlowAlignmentTests
{
    [Theory]
    [InlineData(FontNumeralAlignment.Tabular, 28)]
    [InlineData(FontNumeralAlignment.Tabular, 36)]
    [InlineData(FontNumeralAlignment.Normal, 28)]
    public void CountdownColonKeepsItsNativeVerticalPosition(FontNumeralAlignment alignment, double size) =>
        SharedStaTestRunner.RunAsync(async () =>
        {
            bool reduce = AnimationConfig.ReduceMotion;
            var system = AnimationConfig.ClientAreaAnimationOverride;
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            var text = new NumberFlowText { Text = "24:54", FontFamily = AppFonts.Get(), FontSize = size,
                FontWeight = FontWeights.Bold, Foreground = Brushes.White, Width = 300, Height = 120 };
            Typography.SetNumeralAlignment(text, alignment);
            var root = new AdornerDecorator { Child = text };
            var window = new Window { Width = 340, Height = 160, ShowActivated = false, ShowInTaskbar = false, Content = root };
            try
            {
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => text.IsLoaded, "countdown colon loaded");
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
                var progress = (DependencyProperty)typeof(NumberFlowText).GetField("ProgressProperty", flags)!.GetValue(null)!;
                var pointer = ((Run)text.Inlines.FirstInline).ContentStart.GetPositionAtOffset(2)!;
                var colon = pointer.GetCharacterRect(LogicalDirection.Forward);
                colon.Width = pointer.GetPositionAtOffset(1)!.GetCharacterRect(LogicalDirection.Forward).X - colon.X;
                var offset = VisualTreeHelper.GetOffset(text);
                colon.Offset(offset.X, offset.Y);
                byte[] ColonPixels(Visual visual)
                {
                    var pixels = Pixels(visual);
                    for (int y = 0; y < 120; y++)
                    for (int x = 0; x < 300; x++)
                        if (x < Math.Ceiling(colon.Left) || x >= Math.Floor(colon.Right))
                            Array.Clear(pixels, (y * 300 + x) * 4, 4);
                    return pixels;
                }
                var original = InkBounds(ColonPixels(text));
                foreach (string value in new[] { "24:53", "24:52", "24:51", "24:50" })
                {
                    text.Text = value;
                    text.UpdateLayout();
                    typeof(NumberFlowText).GetMethod("Present", flags)!.Invoke(text, null);
                    text.BeginAnimation(progress, null);
                    foreach (double frame in new[] { 0d, .15, .5, .85, 1 })
                    {
                        text.SetValue(progress, frame);
                        var visual = new DrawingVisual();
                        TextOptions.SetTextRenderingMode(visual, TextOptions.GetTextRenderingMode(text));
                        TextOptions.SetTextHintingMode(visual, TextOptions.GetTextHintingMode(text));
                        using (var dc = visual.RenderOpen())
                        {
                            dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
                            typeof(NumberFlowText).GetMethod("RenderReels", flags)!.Invoke(text, [dc]);
                        }
                        Assert.Equal(original, InkBounds(ColonPixels(visual)));
                    }
                    text.Finish();
                    text.UpdateLayout();
                    Assert.Equal(original, InkBounds(ColonPixels(text)));
                }
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(reduce);
                AnimationConfig.ClientAreaAnimationOverride = system;
            }
        });

    [Theory]
    [InlineData("99%", "98%", false, FontNumeralAlignment.Tabular)]
    [InlineData("82% left", "79% left", true, FontNumeralAlignment.Tabular)]
    [InlineData("12\n59", "13\n00", false, FontNumeralAlignment.Tabular)]
    [InlineData("01:00", "00:59", false, FontNumeralAlignment.Tabular)]
    [InlineData("8", "1", false, FontNumeralAlignment.Proportional)]
    [InlineData("1", "8", false, FontNumeralAlignment.Proportional)]
    public void ReelEndpointsMatchNativeTextPosition(string previous, string next, bool mixed, FontNumeralAlignment alignment) =>
        SharedStaTestRunner.RunAsync(async () =>
        {
            bool reduce = AnimationConfig.ReduceMotion;
            var system = AnimationConfig.ClientAreaAnimationOverride;
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            var text = new NumberFlowText { Text = previous, FontFamily = AppFonts.Get(), FontSize = 28,
                FontWeight = FontWeights.Bold, Foreground = Brushes.White, Width = 300, Height = 120 };
            Typography.SetNumeralAlignment(text, alignment);
            if (mixed) AiUsageTypography.EmphasizePercent(text, previous, 82, 40);
            var window = new Window { Width = 340, Height = 160, ShowActivated = false, ShowInTaskbar = false,
                Content = new AdornerDecorator { Child = text } };
            try
            {
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => text.IsLoaded, "alignment text loaded");
                var original = Pixels(text);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
                var progress = (DependencyProperty)typeof(NumberFlowText).GetField("ProgressProperty", flags)!.GetValue(null)!;
                void Present(string value)
                {
                    if (mixed) ((Run)text.Inlines.FirstInline).Text = value[..3];
                    else text.Text = value;
                    text.UpdateLayout();
                    typeof(NumberFlowText).GetMethod("Present", flags)!.Invoke(text, null);
                    text.BeginAnimation(progress, null);
                }
                byte[] Frame(double value)
                {
                    text.SetValue(progress, value);
                    var visual = new DrawingVisual();
                    TextOptions.SetTextRenderingMode(visual, TextOptions.GetTextRenderingMode(text));
                    TextOptions.SetTextHintingMode(visual, TextOptions.GetTextHintingMode(text));
                    using (var dc = visual.RenderOpen())
                    {
                        var offset = VisualTreeHelper.GetOffset(text);
                        dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
                        typeof(NumberFlowText).GetMethod("RenderReels", flags)!.Invoke(text, [dc]);
                    }
                    return Pixels(visual);
                }
                Present(next);
                AssertMatches(original, Frame(0));
                var animated = Frame(1);
                text.Finish();
                text.UpdateLayout();
                var native = Pixels(text);
                AssertMatches(native, animated);
                Present(previous);
                var interrupted = Frame(.37);
                Present(next);
                AssertMatches(interrupted, Frame(0));
                text.Finish();

                // A clipped field must retain the native ellipsis instead of
                // handing over to a row whose digits do not fit.
                text.Width = 20;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                Present(previous + " long label");
                Assert.False(text.IsAnimating);
                Assert.Null(text.OpacityMask);
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(reduce);
                AnimationConfig.ClientAreaAnimationOverride = system;
            }
        });

    private static void AssertMatches(byte[] native, byte[] animated)
    {
        if (Environment.GetEnvironmentVariable("VNOTCH_NUMBER_FLOW_PREVIEW") is { } directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            foreach (var (name, pixels) in new[] { ("native", native), ("reel", animated) })
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(300, 120, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, 1200)));
                using var file = System.IO.File.Create(System.IO.Path.Combine(directory, $"alignment-{name}.png"));
                encoder.Save(file);
            }
        }
        Assert.Equal(InkBounds(native), InkBounds(animated));
        // WPF can rasterize a single glyph and a complete run differently. Check
        // the ink position, allowing only subpixel antialiasing differences.
        var delta = InkCenter(native) - InkCenter(animated);
        Assert.InRange(Math.Abs(delta.X), 0, .35);
        Assert.InRange(Math.Abs(delta.Y), 0, .35);
    }

    private static Point InkCenter(byte[] pixels)
    {
        double mass = 0, horizontal = 0, vertical = 0;
        for (int y = 0; y < 120; y++)
        for (int x = 0; x < 300; x++)
        {
            double alpha = pixels[(y * 300 + x) * 4 + 3];
            mass += alpha; horizontal += x * alpha; vertical += y * alpha;
        }
        return new Point(horizontal / mass, vertical / mass);
    }

    private static byte[] Pixels(Visual visual)
    {
        var bitmap = new RenderTargetBitmap(300, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[300 * 120 * 4];
        bitmap.CopyPixels(pixels, 300 * 4, 0);
        return pixels;
    }

    private static Rect InkBounds(byte[] pixels)
    {
        int left = 300, top = 120, right = -1, bottom = -1;
        for (int y = 0; y < 120; y++)
        for (int x = 0; x < 300; x++)
        {
            if (pixels[(y * 300 + x) * 4 + 3] < 128) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        Assert.True(right >= left, "Rendered text must contain visible pixels");
        return new Rect(left, top, right - left + 1, bottom - top + 1);
    }
}
