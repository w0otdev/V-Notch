using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NumberFlowPerformanceTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData("99%", "100%")]
    [InlineData("100%", "99%")]
    [InlineData("01:00", "00:59")]
    [InlineData("9.9 MB/s", "12.4 MB/s")]
    [InlineData("12.4 MB/s", "9.9 MB/s")]
    [InlineData("12\n59", "13\n00")]
    [InlineData("١٢%", "١٣%")]
    [InlineData("82% left", "79% left")]
    [InlineData("82% left", "100% left")]
    public void RetainedReelsLandOnNativeTextIncludingInterruption(string previous, string next) => SharedStaTestRunner.RunAsync(async ct =>
    {
        // The legacy renderer ignored tabular typography and snapped changing
        // slot widths. Use fixed-width glyphs for display-list parity; native
        // proportional/tabular positions have their own regression tests.
        using var host = new Pair(previous, legacyMetrics: true);
        await host.Ready(ct);
        host.After.Present(next);
        CompareFrames();
        host.After.Progress(.37);
        host.After.Present(previous);
        CompareFrames();

        void CompareFrames()
        {
            // The old renderer is an allocation baseline, not a visual oracle:
            // it reproduces the missing suffix and incorrect numeral metrics.
            var native = VisualTreeHelper.GetDrawing(host.After.View)!;
            foreach (double progress in new[] { 1d, .99999 })
            {
                host.After.Progress(progress);
                var drawing = new DrawingGroup();
                using (var dc = drawing.Open()) host.After.Render(dc);
                foreach (double dpi in new[] { 96d, 120, 144 })
                    NumberFlowLayoutTests.AssertSamePixels(native, drawing, dpi, $"{previous}->{next}, p={progress}");
            }
        }
    });

    [Fact]
    public void AnimationFramesReuseTheAdornerDisplayList() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var host = new Pair("99%");
        await host.Ready(ct);
        host.Present("100%");
        var view = host.After.View;
        var adorner = typeof(NumberFlowText).GetField("_adorner", Private)!.GetValue(view)!;
        var renders = adorner.GetType().GetProperty("RenderCount", Private)!;
        await WpfFrameWaiter.UntilAsync(() => (int)renders.GetValue(adorner)! > 0, "adorner recorded", ct);
        await WpfFrameWaiter.NextAsync(ct);
        await WpfFrameWaiter.NextAsync(ct);
        int count = (int)renders.GetValue(adorner)!;
        foreach (double progress in new[] { .1, .3, .5, .7, .9 })
        {
            host.After.Progress(progress);
            await WpfFrameWaiter.NextAsync(ct);
        }
        Assert.Equal(count, (int)renders.GetValue(adorner)!);
        output.WriteLine($"Adorner display lists: {count} before and {(int)renders.GetValue(adorner)!} after five presented frames");
    });

    [Fact]
    public void FrameAndRetargetWorkUseLessAllocationThanThePreviousRenderer() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var host = new Pair("9.9 / 16 GB");
        await host.Ready(ct);
        host.Present("10.0 / 16 GB");
        // Prime text shaping/JIT and the retained display lists on both paths.
        MeasureFrames(host.Before, retained: false);
        MeasureFrames(host.After, retained: true);
        var before = new List<Measurement>();
        var after = new List<Measurement>();
        for (int i = 0; i < 7; i++)
        {
            if ((i & 1) == 0)
            {
                before.Add(MeasureFrames(host.Before, false));
                after.Add(MeasureFrames(host.After, true));
            }
            else
            {
                after.Add(MeasureFrames(host.After, true));
                before.Add(MeasureFrames(host.Before, false));
            }
        }
        double oldBytes = before.Average(m => m.Bytes), newBytes = after.Average(m => m.Bytes);
        double oldTime = before.Select(m => m.Microseconds).Order().ElementAt(3);
        double newTime = after.Select(m => m.Microseconds).Order().ElementAt(3);
        output.WriteLine($"Managed animation frame: before {oldBytes:F0} B / {oldTime:F2} us; after {newBytes:F0} B / {newTime:F2} us");
        Assert.True(newBytes < oldBytes * .5, $"Frame allocations: before={oldBytes}, after={newBytes}");

        var oldUpdates = MeasureUpdates(host.Before);
        var newUpdates = MeasureUpdates(host.After);
        output.WriteLine($"Text retarget: before {oldUpdates.Bytes:F0} B / {oldUpdates.Microseconds:F2} us; after {newUpdates.Bytes:F0} B / {newUpdates.Microseconds:F2} us");
        Assert.True(newUpdates.Bytes < oldUpdates.Bytes * .8);
    });

    [Fact]
    public void GlyphCachePreparesOnlyRollingCharactersAndIsBoundedAndReleasedOnUnload() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var host = new Pair("2026");
        await host.Ready(ct);
        var cache = (NumberFlowGlyphCache)typeof(NumberFlowText).GetField("_glyphCache", Private)!.GetValue(host.After.View)!;
        Assert.Equal(0, cache.Count); // Resting glyphs come directly from the native drawing.
        for (int i = 0; i < 30; i++)
        {
            host.After.View.FontSize = 10 + i;
            host.After.Present((2027 + i).ToString());
            Assert.InRange(cache.Count, 1, 128);
        }
        host.Window.Close();
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(0, cache.Count);
        Assert.False(((NumberFlowText)host.After.View).IsAnimating);
        Assert.Null(host.After.View.OpacityMask);
    });

    [Fact]
    public void AForegroundBrushCanChangeColorDuringTheRetainedAnimation() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var host = new Pair("28%");
        await host.Ready(ct);
        var brush = new SolidColorBrush(Colors.Blue);
        host.After.View.Foreground = brush;
        host.After.Present("39%");
        host.After.Progress(.37);
        var before = Pixels(host.After, 96);
        brush.Color = Colors.Red;
        var after = Pixels(host.After, 96);
        Assert.True(Channel(before, 0) > 0);
        Assert.Equal(0, Channel(before, 2));
        Assert.Equal(0, Channel(after, 0));
        Assert.True(Channel(after, 2) > 0);

        static long Channel(byte[] pixels, int channel)
        {
            long total = 0;
            for (int i = channel; i < pixels.Length; i += 4) total += pixels[i];
            return total;
        }
    });

    private static Measurement MeasureFrames(Probe probe, bool retained)
    {
        const int frames = 80;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) probe.Render(dc);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int frame = 0; frame < frames; frame++)
        {
            probe.Progress((frame + .5) / frames);
            if (!retained)
            {
                using var dc = visual.RenderOpen();
                probe.Render(dc);
            }
        }
        return new((GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)frames,
            Stopwatch.GetElapsedTime(started).TotalMicroseconds / frames);
    }

    private static Measurement MeasureUpdates(Probe probe)
    {
        const int iterations = 16;
        probe.Present("9.9 / 16 GB");
        probe.Present("10.0 / 16 GB");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) probe.Present((i & 1) == 0 ? "9.9 / 16 GB" : "10.0 / 16 GB");
        return new((GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations,
            Stopwatch.GetElapsedTime(started).TotalMicroseconds / iterations);
    }

    private static byte[] Pixels(Probe probe, double dpi)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) probe.Render(dc);
        int width = (int)(360 * dpi / 96), height = (int)(120 * dpi / 96);
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private readonly record struct Measurement(double Bytes, double Microseconds);

    private sealed class Probe
    {
        internal TextBlock View { get; }
        private readonly DependencyProperty _progress;
        private readonly Action _present;
        internal Action<DrawingContext> Render { get; }

        internal Probe(TextBlock view)
        {
            View = view;
            var type = view.GetType();
            _progress = (DependencyProperty)type.GetField("ProgressProperty", Private)!.GetValue(null)!;
            _present = type.GetMethod("Present", Private)!.CreateDelegate<Action>(view);
            Render = type.GetMethod("RenderReels", Private)!.CreateDelegate<Action<DrawingContext>>(view);
        }
        internal void Present(string text)
        {
            if (text.EndsWith(" left", StringComparison.Ordinal) && View.Inlines.Count > 1 && View.Inlines.FirstInline is Run number)
                number.Text = text[..(text.IndexOf('%') + 1)];
            else
                View.Text = text;
            View.UpdateLayout();
            _present();
            View.BeginAnimation(_progress, null);
            Progress(0);
        }
        internal void Progress(double progress) => View.SetValue(_progress, progress);
    }

    private sealed class Pair : IDisposable
    {
        private readonly bool _reduce = AnimationConfig.ReduceMotion;
        private readonly bool? _system = AnimationConfig.ClientAreaAnimationOverride;
        internal Window Window { get; }
        internal Probe Before { get; }
        internal Probe After { get; }

        internal Pair(string text, bool legacyMetrics = false)
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.ClientAreaAnimationOverride = true;
            Before = new Probe(new BeforeNumberFlowText());
            After = new Probe(new NumberFlowText());
            var panel = new Grid();
            foreach (var probe in new[] { Before, After })
            {
                var view = probe.View;
                view.Text = text;
                view.FontFamily = legacyMetrics ? new FontFamily("Courier New") : AppFonts.Get();
                view.FontSize = 28;
                view.FontWeight = FontWeights.Bold;
                view.Foreground = Brushes.White;
                view.HorizontalAlignment = HorizontalAlignment.Left;
                if (text.Contains("left")) AiUsageTypography.EmphasizePercent(view, text, 82, 40);
                if (legacyMetrics) Typography.SetNumeralAlignment(view, FontNumeralAlignment.Normal);
                panel.Children.Add(view);
            }
            Window = new Window { Width = 380, Height = 160, ShowActivated = false,
                Content = new AdornerDecorator { Child = panel } };
            Window.Show();
        }

        internal Task Ready(CancellationToken ct) => WpfFrameWaiter.UntilAsync(() =>
            Before.View.IsLoaded && After.View.IsLoaded && After.View.ActualWidth > 0, "number-flow pair loaded", ct);
        internal void Present(string text) { Before.Present(text); After.Present(text); }
        internal void SetProgress(double progress) { Before.Progress(progress); After.Progress(progress); }
        public void Dispose()
        {
            Window.Close();
            AnimationConfig.SetReduceMotion(_reduce);
            AnimationConfig.ClientAreaAnimationOverride = _system;
        }
    }
}
