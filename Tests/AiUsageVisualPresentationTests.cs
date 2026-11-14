using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AiUsageVisualPresentationTests
{
    [Fact]
    public void ExistingQuotaBarsFollowTheAppAccentAtEveryUsageLevel() => SharedStaTestRunner.RunAsync(async () =>
    {
        BackgroundTestWindows.EnsureApplicationResources();
        var original = AiUsageAppearance.Current;
        string originalLanguage = Loc.GetCulture().TwoLetterISOLanguageName;
        string[] keys = ["AccentBrush", "AccentBrushStrong", "AccentForegroundBrush", "AccentHoverBrush",
            "AccentSubtleBrush", "AccentNavHoverBrush", "AccentNavHoverBorderBrush"];
        var resources = Application.Current.Resources;
        var saved = keys.ToDictionary(key => key, key => resources[key]);
        Window? host = null;
        try
        {
            AiUsageAppearance.Apply(new AiUsageAppearance { MotionIntensity = 0 });
            AccentTheme.Apply("pink");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var codex = new AiAccountUsage("Codex", AiUsageState.Ready, now,
                [new("aiUsage.primary", 81, 300, now.AddHours(1)), new("aiUsage.secondary", 31, 10080, now.AddDays(2))]);
            var view = new AiUsageNotchView();
            var widget = new AiUsageStatWidget { IsPreview = true };
            var content = new StackPanel();
            content.Children.Add(view);
            content.Children.Add(widget);
            host = new Window { Content = content, Width = 500, Height = 400, ShowActivated = false };
            BackgroundTestWindows.ProtectInput(host);
            host.Show();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.Present(codex, new("Claude", AiUsageState.Waiting), now);
            var pane = Assert.IsType<Grid>(view.Content).Children.OfType<Grid>().First();
            var row = Assert.IsType<StackPanel>(Assert.IsType<StackPanel>(pane.Children[1]).Children[0]);
            var viewFill = Assert.IsType<Border>(Assert.IsType<Grid>(Assert.IsType<Border>(row.Children[1]).Child).Children[0]);
            widget.Present(codex, new("Claude", AiUsageState.Waiting), now, default(ClaudeBridgeSetupStatus));
            var block = Assert.IsType<StackPanel>(Assert.IsType<StackPanel>(widget.Content).Children[0]);
            var widgetFill = AiUsageWidgetTestUi.Find<Border>(block, "PrimaryQuotaFill");
            var weeklyFill = AiUsageWidgetTestUi.Find<Border>(block, "WeeklyQuotaFill");
            if (SystemParameters.HighContrast)
            {
                Assert.Same(SystemColors.HighlightBrush, viewFill.Background);
                return;
            }
            Assert.Same(resources["AccentBrush"], viewFill.Background);
            Assert.Same(resources["AccentBrush"], widgetFill.Background);
            Assert.Same(widgetFill.Background, weeklyFill.Background);
            AccentTheme.Apply("blue");
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Same(resources["AccentBrush"], viewFill.Background);
            Assert.Same(resources["AccentBrush"], widgetFill.Background);
            Assert.Same(widgetFill.Background, weeklyFill.Background);
            foreach (double used in new[] { 0d, 69, 70, 81, 90, 100 })
                Assert.Same(resources["AccentBrush"], AiUsageAppearance.Current.Fill("Codex", used));
            Layout(view, 464);
            Layout(widget, 130);
            Save(view, "view-app-accent-blue");
            Save(widget, "widget-app-accent-blue");
            var custom = new AiUsageAppearance { StatusColors = false, CodexColor = "#ABCDEF" };
            custom.BindFill(widgetFill, "Codex", 23);
            AccentTheme.Apply("pink");
            Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF), Assert.IsType<SolidColorBrush>(widgetFill.Background).Color);
            foreach (string language in new[] { "en", "vi" })
            {
                Loc.SetLanguage(language);
                view.Present(codex, new("Claude", AiUsageState.Disabled), now);
                await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Layout(view, 464);
                Save(view, "view-loaded-" + language);
            }
        }
        finally
        {
            host?.Close();
            AiUsageAppearance.Apply(original);
            Loc.SetLanguage(originalLanguage);
            foreach (var key in keys) resources[key] = saved[key];
        }
    });

    [Fact]
    public void ProviderViewsAndCompactWidgetsFitTheirHostsAcrossPresentationStates() => SharedStaTestRunner.Run(() =>
    {
        var original = AiUsageAppearance.Current;
        bool reduced = AnimationConfig.ReduceMotion;
        try
        {
            BackgroundTestWindows.EnsureApplicationResources();
            AnimationConfig.SetReduceMotion(true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var codex = new AiAccountUsage("Codex", AiUsageState.Ready, now,
                [new("aiUsage.primary", 23, 300, now.AddMinutes(94)), new("aiUsage.secondary", 58, 10080, now.AddDays(3))], "plus");
            var claude = new AiAccountUsage("Claude", AiUsageState.Ready, now,
                [new("aiUsage.fiveHour", 46, 300, now.AddMinutes(42)), new("aiUsage.sevenDay", 84, 10080, now.AddDays(2))], "pro");
            foreach (string language in new[] { "en", "vi" })
            foreach (string state in new[] { "ready", "compact", "used", "waiting", "single", "disconnected", "claude-first", "used-single", "loading", "unavailable", "expired", "hidden" })
            {
                Loc.SetLanguage(language);
                AiUsageAppearance.Apply(new AiUsageAppearance
                {
                    Compact = state == "compact", ShowUsed = state is "used" or "used-single", ClaudeFirst = state == "claude-first",
                    ShowCodex = state != "hidden", ShowClaude = state is not ("single" or "used-single" or "hidden"), MotionIntensity = 0
                });
                var first = state switch
                {
                    "loading" => new AiAccountUsage("Codex", AiUsageState.Loading),
                    "unavailable" => new AiAccountUsage("Codex", AiUsageState.Unavailable),
                    "expired" => codex with { Windows = [new("aiUsage.primary", 23, 300, now.AddMinutes(-1))] },
                    _ => codex
                };
                var second = state switch
                {
                    "waiting" => new AiAccountUsage("Claude", AiUsageState.Waiting),
                    "disconnected" or "claude-first" => new AiAccountUsage("Claude", AiUsageState.Disabled),
                    "loading" => new AiAccountUsage("Claude", AiUsageState.Loading),
                    "unavailable" => new AiAccountUsage("Claude", AiUsageState.Unavailable),
                    _ => claude
                };
                var view = new AiUsageNotchView();
                view.Present(first, second, now);
                Layout(view, 464);
                Assert.InRange(view.ActualHeight, 70, 240);
                foreach (var pane in Assert.IsType<Grid>(view.Content).Children.OfType<Grid>())
                {
                    var origin = pane.TranslatePoint(new Point(), view);
                    Assert.True(origin.X >= 0 && origin.Y >= 0, language + ":" + state);
                    Assert.True(origin.Y + pane.ActualHeight <= view.ActualHeight + 1, language + ":" + state);
                }
                Save(view, "view-" + state + "-" + language);

                var widget = new AiUsageStatWidget();
                widget.Present(first, second, now, new(true, true, true, false));
                Layout(widget, 130);
                Assert.InRange(widget.ActualHeight, 24, 110);
                Assert.True(widget.DesiredSize.Width <= 130);
                Save(widget, "widget-" + state + "-" + language);
            }
        }
        finally
        {
            AiUsageAppearance.Apply(original);
            AnimationConfig.SetReduceMotion(reduced);
            Loc.SetLanguage("en");
        }
    });

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }

    private static void Save(FrameworkElement element, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_AI_DESIGN_FRAMES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 3),
            (int)Math.Ceiling(element.ActualHeight * 3), 288, 288, PixelFormats.Pbgra32);
        // Render real controls on the same black surface as the notch.
        var surface = new DrawingVisual();
        using (var context = surface.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            context.DrawRectangle(Brushes.Black, null, bounds);
            context.DrawRectangle(new VisualBrush(element)
            {
                AutoLayoutContent = false,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                Stretch = Stretch.Fill
            }, null, bounds);
        }
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
