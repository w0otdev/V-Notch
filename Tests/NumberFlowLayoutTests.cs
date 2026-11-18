using System.IO;
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
public sealed class NumberFlowLayoutTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly DependencyProperty Progress = (DependencyProperty)typeof(NumberFlowText)
        .GetField("ProgressProperty", Private)!.GetValue(null)!;

    [Theory]
    [InlineData("25:00", "24:59", true)]
    [InlineData("09:59", "10:00", true)]
    [InlineData("99%", "100%", false)]
    [InlineData("100%", "99%", false)]
    [InlineData("12\n59", "13\n00", false)]
    [InlineData("82% left", "79% left", false)]
    [InlineData("١٢%", "١٣%", false)]
    public void LastAnimationFrameMatchesNativeText(string previous, string next, bool tabular) =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reduce = AnimationConfig.ReduceMotion;
            bool? system = AnimationConfig.ClientAreaAnimationOverride;
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            var view = new NumberFlowText
            {
                Text = previous, FontFamily = AppFonts.Get(), FontSize = 28,
                FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (tabular) view.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            if (previous.EndsWith(" left")) AiUsageTypography.EmphasizePercent(view, previous, 82, 40);
            var window = new Window
            {
                Width = 400, Height = 180, ShowActivated = false, ShowInTaskbar = false,
                Content = new AdornerDecorator { Child = view }
            };
            try
            {
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => view.IsLoaded && view.IsArrangeValid, "number layout", ct);
                if (next.EndsWith(" left")) ((Run)view.Inlines.FirstInline).Text = next[..(next.IndexOf('%') + 1)];
                else view.Text = next;
                await WpfFrameWaiter.UntilAsync(() => view.IsAnimating, "number animation", ct);
                view.BeginAnimation(Progress, null);
                view.SetValue(Progress, 1d);
                var native = VisualTreeHelper.GetDrawing(view)!;
                var animated = new DrawingGroup();
                using (var dc = animated.Open())
                    typeof(NumberFlowText).GetMethod("RenderReels", Private)!
                        .CreateDelegate<Action<DrawingContext>>(view)(dc);
                foreach (double dpi in new[] { 96d, 120, 144, 192 })
                    AssertSamePixels(native, animated, dpi, $"{previous}->{next}");
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(reduce);
                AnimationConfig.ClientAreaAnimationOverride = system;
            }
        });

    [Theory]
    [InlineData(HorizontalAlignment.Left)]
    [InlineData(HorizontalAlignment.Center)]
    public void ShrinkingValuesKeepPercentVisibleThroughoutTheRoll(HorizontalAlignment alignment) =>
        SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reduce = AnimationConfig.ReduceMotion;
            bool? system = AnimationConfig.ClientAreaAnimationOverride;
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            var view = new NumberFlowText
            {
                Text = "100%", FontFamily = AppFonts.Get(), FontSize = 28, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Center
            };
            var root = new AdornerDecorator { Child = view };
            var window = new Window { Content = root, Width = 400, Height = 160, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => view.IsLoaded && view.IsArrangeValid, "percent layout", ct);
                Rect PercentBounds()
                {
                    var run = (Run)view.Inlines.FirstInline;
                    var bounds = run.ContentStart.GetPositionAtOffset(run.Text.IndexOf('%'))!
                        .GetCharacterRect(LogicalDirection.Forward);
                    bounds.Union(run.ContentEnd.GetCharacterRect(LogicalDirection.Backward));
                    return bounds;
                }
                Rect previous = view.TransformToVisual(root).TransformBounds(PercentBounds());
                view.Text = "9%";
                await WpfFrameWaiter.UntilAsync(() => view.IsAnimating, "shrinking percent animation", ct);
                view.BeginAnimation(Progress, null);
                Rect targetLocal = PercentBounds();
                Rect target = view.TransformToVisual(root).TransformBounds(targetLocal);
                var native = VisualTreeHelper.GetDrawing(view)!;
                long Ink(Drawing drawing, Rect area)
                {
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.PushTransform(new TranslateTransform(-area.X + 2, -area.Y + 2));
                        dc.PushClip(new RectangleGeometry(area));
                        dc.DrawDrawing(drawing);
                    }
                    int width = (int)Math.Ceiling(area.Width) + 4, height = (int)Math.Ceiling(area.Height) + 4;
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var pixels = new byte[width * height * 4];
                    bitmap.CopyPixels(pixels, width * 4, 0);
                    return pixels.Sum(b => (long)b);
                }
                long expected = Ink(native, targetLocal);
                Assert.True(expected > 0);
                foreach (double progress in new[] { 0d, .1, .37, .71, .99, 1 })
                {
                    view.SetValue(Progress, progress);
                    var frame = new DrawingGroup();
                    using (var dc = frame.Open()) typeof(NumberFlowText).GetMethod("RenderReels", Private)!
                        .CreateDelegate<Action<DrawingContext>>(view)(dc);
                    Rect area = targetLocal;
                    area.Offset((previous.X - target.X) * (1 - progress), (previous.Y - target.Y) * (1 - progress));
                    long actual = Ink(frame, area);
                    Assert.True(actual >= expected * .97, $"Percent clipped at p={progress}, alignment={alignment}: {actual}/{expected}");
                }
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(reduce);
                AnimationConfig.ClientAreaAnimationOverride = system;
            }
        });

    internal static void AssertSamePixels(Drawing expected, Drawing actual, double dpi, string description)
    {
        byte[] Pixels(Drawing drawing)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
            int width = (int)(360 * dpi / 96), height = (int)(120 * dpi / 96);
            var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }
        byte[] before = Pixels(expected), after = Pixels(actual);
        long difference = 0, ink = 0;
        for (int i = 0; i < before.Length; i++)
        {
            difference += Math.Abs(before[i] - after[i]);
            ink += before[i];
        }
        Assert.True(ink > 0, "Native text must be visible.");
        Assert.True(difference / (double)ink < .015,
            $"{description}, dpi={dpi}: animation/native ink difference = {difference / (double)ink:P2}");
    }

    [Fact]
    public void RealCountdownRetargetsAndReturnsToTheSameScreenPixels() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool reduce = AnimationConfig.ReduceMotion;
        bool? system = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.SetReduceMotion(false);
        AnimationConfig.ClientAreaAnimationOverride = true;
        using var countdown = new CountdownView();
        var view = (NumberFlowText)countdown.FindName("CountdownDisplay");
        view.Text = "25:00";
        var root = new AdornerDecorator { Child = countdown };
        var window = new Window
        {
            Content = root, Width = 720, Height = 140, Background = Brushes.Black,
            ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => view.IsLoaded && view.IsArrangeValid, "real countdown", ct);
            foreach (string value in new[] { "24:59", "24:58", "25:58", "26:58", "25:58", "25:00" })
            {
                view.Text = value;
                await WpfFrameWaiter.UntilAsync(() => view.IsAnimating &&
                    (string?)typeof(NumberFlowText).GetField("_lastText", Private)!.GetValue(view) == value,
                    "real countdown animation " + value, ct);
                view.BeginAnimation(Progress, null);
                view.SetValue(Progress, .37);
            }
            view.SetValue(Progress, 1d);
            await WpfFrameWaiter.NextAsync(ct);
            RenderTargetBitmap Capture(string name)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
                    (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                if (Environment.GetEnvironmentVariable("VNOTCH_NUMBER_FLOW_PREVIEW") is { } output)
                {
                    Directory.CreateDirectory(output);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, name + ".png"));
                    encoder.Save(file);
                }
                return bitmap;
            }
            var animated = Capture("countdown-animation-end");
            view.Finish();
            await WpfFrameWaiter.NextAsync(ct);
            var native = Capture("countdown-native");
            Assert.False(view.IsAnimating);
            Assert.Null(view.OpacityMask);
            Rect bounds = view.TransformToVisual(root).TransformBounds(new Rect(view.RenderSize));
            bounds.Inflate(2, 2);
            var crop = new Int32Rect((int)bounds.X, (int)bounds.Y, (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
            byte[] before = new byte[crop.Width * crop.Height * 4], after = new byte[before.Length];
            animated.CopyPixels(crop, before, crop.Width * 4, 0);
            native.CopyPixels(crop, after, crop.Width * 4, 0);
            long difference = 0, ink = 0;
            for (int i = 0; i < before.Length; i += 4)
            {
                difference += Math.Abs(before[i] - after[i]);
                ink += after[i];
            }
            Assert.True(ink > 0);
            Assert.True(difference / (double)ink < .02, $"Countdown screen pixels moved by {difference / (double)ink:P2}");
        }
        finally
        {
            window.Close();
            AnimationConfig.SetReduceMotion(reduce);
            AnimationConfig.ClientAreaAnimationOverride = system;
        }
    });
}
