using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class HotPathRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-match")]
    public void UnmatchedTextUsesPlainTextStorage(string query) => SharedStaTestRunner.Run(() =>
    {
        var block = new TextBlock();
        HighlightedText.SetText(block, "Hello world");
        HighlightedText.SetQuery(block, "hello");
        HighlightedText.SetQuery(block, query);
        Assert.Equal("Hello world", block.Text);
        // TextBlock internally exposes a Run even for plain Text. Its local value
        // distinguishes the simple text store from an explicitly built inline tree.
        Assert.Equal("Hello world", block.ReadLocalValue(TextBlock.TextProperty));
    });

    [Theory]
    [InlineData("banana", "ana", "011111")]
    [InlineData("Hello WORLD", " world  HEL ", "11100011111")]
    [InlineData("test", "", "0000")]
    public void HighlightingPreservesOverlapsAndCaseInsensitiveTokens(string text, string query, string expected)
    {
        Assert.Equal(expected, string.Concat(HighlightedText.BuildMatchMask(text, query).Select(b => b ? '1' : '0')));
    }

    [Fact]
    public void HighlightColorTracksResourceReplacement() => SharedStaTestRunner.Run(() =>
    {
        var block = new TextBlock();
        block.Resources["AccentBrush"] = Brushes.Red;
        HighlightedText.SetText(block, "Hello world");
        HighlightedText.SetQuery(block, "hello");
        var highlighted = Assert.IsType<Run>(block.Inlines.FirstInline);
        Assert.Same(Brushes.Red, highlighted.Foreground);
        block.Resources["AccentBrush"] = Brushes.Blue;
        Assert.Same(Brushes.Blue, highlighted.Foreground);
    });

    [Fact]
    public void LongHighlightTextReusesRunsWithoutStalePooledMatches() => SharedStaTestRunner.Run(() =>
    {
        var block = new TextBlock();
        HighlightedText.SetText(block, new string('a', 600) + " target");
        HighlightedText.SetQuery(block, "target");
        HighlightedText.SetQuery(block, "aa");
        var runs = block.Inlines.Cast<Run>().ToArray();
        Assert.Equal(new string('a', 600) + " target", string.Concat(runs.Select(r => r.Text)));
        Assert.Equal(new string('a', 600), Assert.Single(runs.Where(r => r.FontWeight == FontWeights.Bold)).Text);
        HighlightedText.SetQuery(block, "target");
        runs = block.Inlines.Cast<Run>().ToArray();
        Assert.Equal("target", Assert.Single(runs.Where(r => r.FontWeight == FontWeights.Bold)).Text);
    });

    [Fact]
    public void MorphGeometryMatchesBaselineAcrossContourCountsAndInterruptedAnimations() => SharedStaTestRunner.Run(() =>
    {
        var optimized = new RenderProbe();
        var baseline = new BaselineRenderProbe();
        var host = new StackPanel();
        host.Children.Add(optimized);
        host.Children.Add(baseline);
        using var source = new HwndSource(new HwndSourceParameters("Morph regression")
        {
            Width = 56, Height = 112, WindowStyle = 0
        }) { RootVisual = host };
        host.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        Assert.True(optimized.IsLoaded && baseline.IsLoaded);
        var shapes = new[]
        {
            Geometry.Parse("M0,0 L48,0 48,48 0,48 Z"),
            Geometry.Parse("M24,0 L48,48 0,48 Z M18,18 L30,18 24,30 Z"),
            Geometry.Parse("M2,2 L46,2 40,46 8,46 Z")
        };
        optimized.MorphTo(shapes[0], false);
        baseline.MorphTo(shapes[0], false);
        foreach (var shape in shapes.Skip(1).Concat(shapes.Take(2)))
        {
            optimized.MorphTo(shape, true);
            baseline.MorphTo(shape, true);
            foreach (double progress in new[] { 0, 0.25, 0.5, 0.9 })
            {
                SetProgress(optimized, typeof(MorphingSettingsIcon), progress);
                SetProgress(baseline, typeof(BaselineMorphingSettingsIcon), progress);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) { optimized.Render(dc); baseline.Render(dc); }
                var actual = GetDisplay(optimized, typeof(MorphingSettingsIcon));
                var expected = GetDisplay(baseline, typeof(BaselineMorphingSettingsIcon));
                Assert.Equal(expected.Bounds, actual.Bounds);
                Assert.True(Math.Abs(expected.GetArea() - actual.GetArea()) < 0.0001);
            }
            // Start the next transition from the in-flight display at 90%.
        }
    });

    private static void SetProgress(FrameworkElement icon, Type type, double value)
    {
        var property = (DependencyProperty)type.GetField("ProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        icon.BeginAnimation(property, null);
        icon.SetValue(property, value);
    }

    private static Geometry GetDisplay(FrameworkElement icon, Type type)
        => (Geometry)type.GetField("_display", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(icon)!;

    [Theory]
    [InlineData("password=sk-abcdefghijk", "password=[REDACTED]")]
    [InlineData("sp_dc=sk-abcdefghijk; api_key=enc:abcdefghijklmnop", "sp_dc=[REDACTED]; api_key=[REDACTED]")]
    [InlineData("https://example.com?token=sk-abcdefghijk&x=1", "https://example.com?token=[REDACTED]&x=1")]
    [InlineData("x-goog-api-key: secret; Authorization: Bearer abc.def", "x-goog-api-key: [REDACTED]; Authorization: Bearer [REDACTED]")]
    public void ScrubberRedactsWholeValuesIncludingNestedTokenFormats(string input, string expected)
        => Assert.Equal(expected, SensitiveDataScrubber.Scrub(input));

    [Fact]
    public void ScrubberBoundsPathologicalInput()
    {
        var input = new string('a', 200_000) + "! api_key=private-value";
        var timer = Stopwatch.StartNew();
        var result = SensitiveDataScrubber.Scrub(input);
        Assert.DoesNotContain("private-value", result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Scrubbing took {timer.Elapsed}.");
    }

    [Fact]
    public void BenchmarkRenderAndHighlightAllocations() => SharedStaTestRunner.Run(() =>
    {
        var baseline = new BaselineRenderProbe();
        double baselineBytes = MeasureMorph(baseline, baseline.Render,
            (g, animate) => baseline.MorphTo(g, animate), typeof(BaselineMorphingSettingsIcon), "baseline morph frame");
        var icon = new RenderProbe();
        double optimizedBytes = MeasureMorph(icon, icon.Render,
            (g, animate) => icon.MorphTo(g, animate), typeof(MorphingSettingsIcon), "optimized morph frame");
        Assert.True(optimizedBytes < baselineBytes / 2, "Morph rendering must substantially reduce frame allocations.");
        var block = new TextBlock();
        int index = 0;
        Measure("empty-query title", 1000, () => HighlightedText.SetText(block, ++index % 2 == 0 ? "Hello world" : "Hello there"));
        HighlightedText.SetQuery(block, "hello world");
        Measure("matched-query title", 1000, () => HighlightedText.SetText(block, ++index % 2 == 0 ? "Hello world" : "Hello there"));
        string input = "sp_dc=cookie123; api_key=my-private-key Authorization: Bearer abc.def password=pass";
        Measure("scrub mixed secrets", 1000, () => SensitiveDataScrubber.Scrub(input));
    });

    private double MeasureMorph(FrameworkElement icon, Action<DrawingContext> render,
        Action<Geometry, bool> morph, Type iconType, string label)
    {
        using var source = new HwndSource(new HwndSourceParameters("Morph benchmark")
        {
            Width = 56, Height = 56, WindowStyle = 0
        }) { RootVisual = icon };
        icon.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        icon.Measure(new Size(56, 56));
        icon.Arrange(new Rect(0, 0, 56, 56));
        Assert.True(icon.IsLoaded);
        morph(Geometry.Parse("M0,0 L48,0 48,48 0,48 Z"), false);
        morph(Geometry.Parse("M24,0 L48,48 0,48 Z M18,18 L30,18 24,30 Z"), true);
        var progress = (DependencyProperty)iconType
            .GetField("ProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        icon.BeginAnimation(progress, null);
        var visual = new DrawingVisual();
        int frame = 0;
        return Measure(label, 1000, () =>
        {
            icon.SetValue(progress, 0.01 + 0.98 * (++frame % 120) / 120);
            using var dc = visual.RenderOpen();
            render(dc);
        });
    }

    private double Measure(string name, int iterations, Action action)
    {
        for (int i = 0; i < 100; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) action();
        double microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds / iterations;
        double allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
        output.WriteLine($"{name}: {microseconds:F2} us/op, {allocated:F0} bytes/op ({iterations} iterations)");
        return allocated;
    }

    private sealed class RenderProbe : MorphingSettingsIcon
    {
        public void Render(DrawingContext dc) => OnRender(dc);
    }

    private sealed class BaselineRenderProbe : BaselineMorphingSettingsIcon
    {
        public void Render(DrawingContext dc) => OnRender(dc);
    }
}
