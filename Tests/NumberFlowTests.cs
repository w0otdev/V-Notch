using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NumberFlowTests
{
    [Theory]
    [InlineData(9, 0, 1, 10)]
    [InlineData(0, 9, -1, -1)]
    [InlineData(3.4, 2, -1, 2)]
    [InlineData(9.6, 2, 1, 12)]
    [InlineData(-.5, 8, -1, -2)]
    [InlineData(5, 5, 1, 5)]
    public void WheelsContinueFromTheirCurrentPosition(double current, int digit, int trend, double expected) =>
        Assert.Equal(expected, NumberFlowValue.Destination(current, digit, trend));

    [Theory]
    [InlineData("99%", "100%")]
    [InlineData("1,999.9 GB", "2,000.0 GB")]
    [InlineData("9.9 / 16 GB", "10.0 / 16 GB")]
    [InlineData("−8°", "−12°")]
    [InlineData("١٢%", "١٣%")]
    public void StableKeysPreserveUnitsAndSeparateNumberGroups(string previous, string next)
    {
        var before = NumberFlowValue.Split(previous, "");
        var after = NumberFlowValue.Split(next, previous);
        Assert.Equal(after.Length, after.Select(p => p.Key).Distinct().Count());
        Assert.Equal(before.First(p => p.Key == "n:0:i:0").Key, after.First(p => p.Key == "n:0:i:0").Key);
        Assert.Equal(next, new string(after.Select(p => p.Character).ToArray()));
    }

    [Fact]
    public void CountdownCarriesUseSixDigitsForSecondsTens()
    {
        var parts = NumberFlowValue.Split("00:59", "01:00");
        Assert.All(parts.Where(p => p.Digit >= 0), p => Assert.Equal(-1, p.Trend));
        Assert.Equal(6, parts[3].Radix);
        Assert.Equal(-1, NumberFlowValue.Destination(0, 5, -1, parts[3].Radix));
        var stacked = NumberFlowValue.Split("13\n00", "12\n59");
        Assert.All(stacked.Where(p => p.Digit >= 0), p => Assert.Equal(1, p.Trend));
        Assert.Equal(6, stacked[3].Radix);
    }

    [Fact]
    public void RebuiltQuotaRowsCarryValuesWithoutReplacingTextOrInlineFormatting() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reduce = AnimationConfig.ReduceMotion;
        var system = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.SetReduceMotion(false);
        AnimationConfig.ClientAreaAnimationOverride = true;
        var panel = new StackPanel();
        var old = new NumberFlowText { Name = "Quota", Text = "82% left", FontSize = 14 };
        panel.Children.Add(old);
        var window = new Window { Content = new AdornerDecorator { Child = panel }, Width = 300, Height = 160, ShowInTaskbar = false };
        try
        {
            window.Show(); await Task.Delay(50);
            var snapshot = new NumberFlowSnapshot();
            snapshot.Capture(panel);
            panel.Children.Clear();
            var next = new NumberFlowText { Name = "Quota", Text = "79% left", FontSize = 14 };
            AiUsageTypography.EmphasizePercent(next, next.Text, 79, 26);
            var numberRun = next.Inlines.FirstInline;
            panel.Children.Add(next);
            snapshot.Restore(panel);
            await WpfFrameWaiter.UntilAsync(() => next.IsAnimating, "rebuilt quota animation");
            Assert.True(next.IsAnimating);
            Assert.Equal("79% left", next.Text);
            Assert.Same(numberRun, next.Inlines.FirstInline);
            Assert.Equal(26, numberRun.FontSize);
            await WpfFrameWaiter.UntilAsync(() => !next.IsAnimating, "rebuilt quota animation completed");
            Assert.False(next.IsAnimating);
            Assert.Null(next.OpacityMask);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduce); AnimationConfig.ClientAreaAnimationOverride = system; }
    });

    [Fact]
    public void BindingAutomationInterruptionAndLifetimeUseTheFinalValue() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reduce = AnimationConfig.ReduceMotion;
        var system = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.SetReduceMotion(false);
        AnimationConfig.ClientAreaAnimationOverride = true;
        var text = new NumberFlowText { FontSize = 36, Foreground = Brushes.White, Text = "99%" };
        var source = new TextBlock { Text = "99%" };
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = source });
        var root = new AdornerDecorator { Child = text };
        var window = new Window { Width = 360, Height = 140, Content = root, ShowInTaskbar = false };
        try
        {
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => text.IsLoaded, "bound number loaded");
            source.Text = "100%";
            await WpfFrameWaiter.UntilAsync(() => text.IsAnimating, "bound number animation");
            Assert.True(text.IsAnimating);
            Assert.Equal("100%", text.Text);
            Assert.Equal("100%", UIElementAutomationPeer.CreatePeerForElement(text)!.GetName());
            Assert.True(BindingOperations.IsDataBound(text, TextBlock.TextProperty));
            Assert.NotNull(text.OpacityMask);
            source.Text = "82%";
            await WpfFrameWaiter.NextAsync();
            Assert.True(text.IsAnimating);
            Assert.Equal("82%", text.Text);
            AnimationConfig.SetReduceMotion(true);
            Assert.False(text.IsAnimating);
            Assert.Null(text.OpacityMask);
            source.Text = "65%";
            await Task.Delay(40);
            Assert.False(text.IsAnimating);
            AnimationConfig.SetReduceMotion(false);
            source.Text = "66%";
            await WpfFrameWaiter.UntilAsync(() => text.IsAnimating, "animation resumed after reduced motion");
            text.Visibility = Visibility.Collapsed;
            Assert.False(text.IsAnimating);
            text.Visibility = Visibility.Visible;
            await Task.Delay(40);
            source.Text = "67%";
            await WpfFrameWaiter.UntilAsync(() => text.IsAnimating, "animation resumed after visibility change");
            Assert.True(text.IsAnimating);
            root.Child = null;
            await Task.Delay(40);
            Assert.False(text.IsAnimating);
            Assert.Null(text.OpacityMask);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduce); AnimationConfig.ClientAreaAnimationOverride = system; }
    });

    [Fact]
    public void RenderRealWpfDigitsIncludingRunsStackedClockAndChangingWidths() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reduce = AnimationConfig.ReduceMotion;
        var system = AnimationConfig.ClientAreaAnimationOverride;
        AnimationConfig.SetReduceMotion(false);
        AnimationConfig.ClientAreaAnimationOverride = true;
        var grid = new Grid { Width = 640, Height = 420, Background = new SolidColorBrush(Color.FromRgb(16, 17, 20)) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new());
        string[] labels = ["Volume", "Countdown", "CPU", "Network", "AI quota", "Clock"];
        string[] initial = ["99%", "01:00", "38%", "9.9 MB/s", "82% left", "12\n59"];
        string[] next = ["100%", "00:59", "52%", "12.4 MB/s", "79% left", "13\n00"];
        var readouts = new List<NumberFlowText>();
        for (int i = 0; i < labels.Length; i++)
        {
            var panel = new StackPanel { Margin = new Thickness(28, 14, 20, 10), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = labels[i], FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 5) });
            var number = new NumberFlowText { Text = initial[i], FontFamily = AppFonts.Get(), FontWeight = FontWeights.Bold,
                FontSize = i == 5 ? 28 : 38, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            if (i == 4) AiUsageTypography.EmphasizePercent(number, initial[i], 82, 44);
            panel.Children.Add(number); readouts.Add(number);
            Grid.SetColumn(panel, i % 2); Grid.SetRow(panel, i / 2); grid.Children.Add(panel);
        }
        var root = new AdornerDecorator { Child = grid };
        var window = new Window { Width = 660, Height = 460, Content = root, ShowInTaskbar = false };
        try
        {
            window.Show(); await Task.Delay(80);
            string? output = Environment.GetEnvironmentVariable("VNOTCH_NUMBER_FLOW_PREVIEW");
            if (output != null) Directory.CreateDirectory(output);
            void Capture(int frame)
            {
                if (output == null) return;
                var bitmap = new RenderTargetBitmap(640, 420, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"frame-{frame:D3}.png")); encoder.Save(file);
            }
            Capture(0);
            for (int i = 0; i < readouts.Count; i++)
            {
                if (i == 4) ((Run)readouts[i].Inlines.FirstInline).Text = "79%";
                else readouts[i].Text = next[i];
            }
            // Presentation now follows WPF's settled layout instead of forcing a
            // synchronous layout flush. Wait for the frame, not a wall-clock guess.
            await WpfFrameWaiter.UntilAsync(() => readouts.All(n => n.IsAnimating), "all number reels started");
            Assert.Equal("79% left", readouts[4].Text);
            Assert.All(readouts, n => Assert.True(n.IsAnimating, n.Text));
            for (int frame = 1; frame <= 22; frame++) { Capture(frame); await Task.Delay(30); }
            Assert.All(readouts, n => Assert.False(n.IsAnimating));
            Assert.All(readouts, n => Assert.Null(n.OpacityMask));
            Capture(23);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduce); AnimationConfig.ClientAreaAnimationOverride = system; }
    });

    [Fact]
    public void MediaProgressReadoutsKeepTheirOriginalTextBlocks() => SharedStaTestRunner.Run(() =>
    {
        var media = new MediaPlaybackView();
        Assert.IsType<TextBlock>(media.FindName("CurrentTimeText"));
        Assert.IsType<TextBlock>(media.FindName("RemainingTimeText"));
        var countdown = new CountdownView();
        Assert.IsType<NumberFlowText>(countdown.FindName("CountdownDisplay"));
        var weather = new WeatherWidget();
        Assert.IsType<NumberFlowText>(weather.FindName("WeatherTempText"));
        weather.Dispose();
    });
}
