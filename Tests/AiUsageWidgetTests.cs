using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Presenters;
using VNotch.Services;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AiUsageWidgetTests
{
    [Theory]
    [InlineData((int)AiUsageState.Disabled, false)]
    [InlineData((int)AiUsageState.Disabled, true)]
    [InlineData((int)AiUsageState.NotInstalled, false)]
    [InlineData((int)AiUsageState.SignedOut, true)]
    [InlineData((int)AiUsageState.Waiting, false)]
    [InlineData((int)AiUsageState.Unsupported, true)]
    public void DisconnectedClaudeLeavesBothCodexQuotaBarsAndBottomClearance(int state, bool bridgeConnected) => SharedStaTestRunner.Run(() =>
    {
        var original = AiUsageAppearance.Current;
        try
        {
            Loc.SetLanguage("en");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (bool claudeFirst in new[] { false, true })
            {
                AiUsageAppearance.Apply(new AiUsageAppearance { ClaudeFirst = claudeFirst, MotionIntensity = 0 });
                var widget = new AiUsageStatWidget();
                widget.Present(new("Codex", AiUsageState.Ready, now,
                    [new("aiUsage.primary", 75, 300, now.AddHours(1)), new("aiUsage.secondary", 30, 10080, now.AddDays(2))], "plus"),
                    new("Claude", (AiUsageState)state), now, new(true, bridgeConnected, bridgeConnected, false));
                widget.Measure(new Size(130, double.PositiveInfinity));
                widget.Arrange(new Rect(0, 0, 130, widget.DesiredSize.Height));
                widget.UpdateLayout();
                var root = Assert.IsType<StackPanel>(widget.Content);
                var block = Assert.IsType<StackPanel>(Assert.Single(root.Children.Cast<UIElement>()));
                Assert.Equal("CodexSubscription", block.Name);
                Assert.Equal("Plus", AiUsageWidgetTestUi.Text(block, "SubscriptionPlan"));
                Assert.Equal("5h", AiUsageWidgetTestUi.Text(block, "PrimaryWindowLabel"));
                Assert.Equal("7d", AiUsageWidgetTestUi.Text(block, "WeeklyWindowLabel"));
                Assert.Equal("25% left", AiUsageWidgetTestUi.Text(block, "PrimaryAllowance"));
                Assert.Equal("70% left", AiUsageWidgetTestUi.Text(block, "WeeklyAllowance"));
                foreach (var (name, fraction) in new[] { ("PrimaryQuotaTrack", .25), ("WeeklyQuotaTrack", .70) })
                {
                    var track = AiUsageWidgetTestUi.Find<Border>(block, name);
                    var fill = Assert.IsType<Border>(track.Child);
                    Assert.InRange(Math.Abs(track.ActualWidth * fraction - fill.ActualWidth),
                        0, 1 / VisualTreeHelper.GetDpi(fill).DpiScaleX);
                }
                var weekly = AiUsageWidgetTestUi.Find<StackPanel>(block, "WeeklyQuota");
                double lastPixel = weekly.TranslatePoint(new Point(0, weekly.ActualHeight), widget).Y;
                Assert.True(widget.ActualHeight - lastPixel >= 9);
                Assert.True(widget.ActualHeight <= 110);
            }
        }
        finally { AiUsageAppearance.Apply(original); }
    });

    [Fact]
    public void WidgetAdaptsWhenClaudeConnectsAndKeepsPlanAndWeeklyColumnsSeparate() => SharedStaTestRunner.Run(() =>
    {
        var original = AiUsageAppearance.Current;
        try
        {
            Loc.SetLanguage("en");
            AiUsageAppearance.Apply(new AiUsageAppearance { MotionIntensity = 0 });
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var codex = new AiAccountUsage("Codex", AiUsageState.Ready, now,
                [new("aiUsage.primary", 75, 300, now.AddHours(1)), new("aiUsage.secondary", 30, 10080, now.AddDays(2))], "plus");
            var claude = new AiAccountUsage("Claude", AiUsageState.Ready, now,
                [new("aiUsage.fiveHour", 40, 300, now.AddHours(1)), new("aiUsage.sevenDay", 20, 10080, now.AddDays(2))], "PRO");
            var widget = new AiUsageStatWidget();
            foreach (bool connected in new[] { false, true, false, true })
            {
                widget.Present(codex, connected ? claude : new("Claude", AiUsageState.Disabled), now,
                    new(true, connected, connected, false));
                var root = Assert.IsType<StackPanel>(widget.Content);
                Assert.Equal(connected ? 2 : 1, root.Children.Count);
                if (!connected) continue;
                foreach (var (index, plan, week) in new[] { (0, "Plus · 5h", "7d 70%"), (1, "Pro · 5h", "7d 80%") })
                {
                    var block = Assert.IsType<StackPanel>(root.Children[index]);
                    Assert.Equal(plan, AiUsageWidgetTestUi.Text(block, "SubscriptionPlan"));
                    var weekly = AiUsageWidgetTestUi.Find<TextBlock>(block, "WeeklySummary");
                    Assert.Equal(week, weekly.Text);
                    Assert.Equal(1, Grid.GetColumn(weekly));
                    Assert.Equal(HorizontalAlignment.Right, weekly.HorizontalAlignment);
                }
            }
        }
        finally { AiUsageAppearance.Apply(original); }
    });

    [Theory]
    [InlineData(false, false, "aiUsage.connectShort")]
    [InlineData(true, true, "aiUsage.apiShort")]
    [InlineData(true, false, "aiUsage.activityShort")]
    public void OnlyConnectedWaitingClaudeKeepsItsNextStepInTheWidget(bool connected, bool api, string label) => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var widget = new AiUsageStatWidget();
        widget.Present(new("Codex", AiUsageState.Ready, now,
            [new("aiUsage.primary", 11, 300, now.AddHours(2)), new("aiUsage.secondary", 2, 10080, now.AddDays(2))]),
            new("Claude", AiUsageState.Waiting), now, new(true, connected, connected, api));
        var root = Assert.IsType<StackPanel>(widget.Content);
        if (!connected || api)
        {
            var only = Assert.IsType<StackPanel>(Assert.Single(root.Children.Cast<UIElement>()));
            Assert.Equal("CodexSubscription", only.Name);
            Assert.Equal("89% left", AiUsageWidgetTestUi.Text(only, "PrimaryAllowance"));
            Assert.Equal("98% left", AiUsageWidgetTestUi.Text(only, "WeeklyAllowance"));
            return;
        }
        var claude = Assert.IsType<StackPanel>(root.Children[1]);
        var header = Assert.IsType<Grid>(claude.Children[0]);
        Assert.Equal("—", Assert.IsType<TextBlock>(header.Children[1]).Text);
        Assert.Equal(Loc.Get(label), AiUsageWidgetTestUi.Text(claude, "SubscriptionPlan"));
        var track = Assert.IsType<Border>(claude.Children[1]);
        var unavailableFill = Assert.IsType<Border>(track.Child);
        widget.Measure(new Size(130, 110));
        widget.Arrange(new Rect(0, 0, 130, 110));
        widget.UpdateLayout();
        Assert.Equal(0, unavailableFill.ActualWidth);
        if (api)
        {
            Assert.Contains(Loc.Get("aiUsage.claudeCustomEndpoint"), Assert.IsType<string>(claude.ToolTip));
            widget.Measure(new Size(130, 110));
            widget.Arrange(new Rect(0, 0, 130, 110));
            widget.UpdateLayout();
            SaveCard(widget, "widget-claude-api.png");
        }
    });

    [Fact]
    public void AccessibleWidgetOpensDetailsOfflineAndSettingsWhenMonitoringIsOff() => SharedStaTestRunner.Run(() =>
    {
        var widget = new AiUsageStatWidget();
        var host = new Host();
        host.Settings.ExpandedWidget = "aiusage";
        host.Settings.EnableAiUsageMonitoring = true;
        host.Settings.EnableLocalOnlyMode = true;
        using var presenter = new ClockWidgetPresenter(host, new ClockWidgetViewRefs
        {
            Window = host.Window, NotchBorder = new Border(), CalendarWidget = new Grid(),
            CalendarStripContainer = new Border(), ClockWidget = new Border(), AiUsageWidgetContent = widget
        });
        presenter.Initialize();
        var peer = UIElementAutomationPeer.CreatePeerForElement(widget)!;
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        Assert.True(widget.Focusable);
        widget.RequestOpenDetails();
        Assert.Equal(1, host.AiUsageOpens);
        host.Settings.EnableAiUsageMonitoring = false;
        widget.RequestOpenDetails();
        Assert.Equal(1, host.AiUsageSettingsOpens);
        presenter.Dispose();
        widget.RequestOpenDetails();
        Assert.Equal(1, host.AiUsageSettingsOpens);
    });

    [Theory]
    [InlineData(0, 100)]
    [InlineData(11, 89)]
    [InlineData(100, 0)]
    public void StatWidgetDrainsFromFullToEmptyAndShowsWeeklyRemaining(double used, double remaining) => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var widget = new AiUsageStatWidget();
        widget.Present(new("Codex", AiUsageState.Ready, now,
            [new("aiUsage.primary", used, 300, now.AddHours(2)), new("aiUsage.secondary", 2, 10080, now.AddDays(2))]),
            new("Claude", AiUsageState.Ready, now,
            [new("aiUsage.fiveHour", used, 300, now.AddHours(2)), new("aiUsage.sevenDay", 2, 10080, now.AddDays(2))]), now);
        var root = Assert.IsType<StackPanel>(widget.Content);
        widget.Measure(new Size(130, 110));
        widget.Arrange(new Rect(0, 0, 130, 110));
        widget.UpdateLayout();
        for (int i = 0; i < 2; i++)
        {
            var block = Assert.IsType<StackPanel>(root.Children[i]);
            var header = Assert.IsType<Grid>(block.Children[0]);
            Assert.Equal(remaining + "% left", Assert.IsType<TextBlock>(header.Children[1]).Text);
            var track = Assert.IsType<Border>(block.Children[1]);
            var fill = Assert.IsType<Border>(track.Child);
            Assert.InRange(Math.Abs(track.ActualWidth * remaining / 100 - fill.ActualWidth),
                0, 1 / VisualTreeHelper.GetDpi(fill).DpiScaleX);
            Assert.Equal("5h", AiUsageWidgetTestUi.Text(block, "SubscriptionPlan"));
            var weekly = AiUsageWidgetTestUi.Find<TextBlock>(block, "WeeklySummary");
            Assert.Equal("7d 98%", weekly.Text);
            Assert.Equal(HorizontalAlignment.Right, weekly.HorizontalAlignment);
            Assert.InRange(Math.Abs(weekly.TranslatePoint(new Point(weekly.ActualWidth, 0), block).X -
                track.TranslatePoint(new Point(track.ActualWidth, 0), block).X), 0, 1 / VisualTreeHelper.GetDpi(track).DpiScaleX);
        }
        if (used == 11)
        {
            widget.Measure(new Size(130, 110));
            widget.Arrange(new Rect(0, 0, 130, 110));
            widget.UpdateLayout();
            SaveCard(widget, "remaining-quota-widget.png");
        }
    });

    [Fact]
    public void NotchViewShowsRemainingAllowanceAndKeepsExpiredBarsEmpty() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var view = new AiUsageNotchView();
        view.Present(new("Codex", AiUsageState.Ready, now,
            [new("aiUsage.primary", 11, 300, now.AddHours(2))]),
            new("Claude", AiUsageState.Ready, now,
            [new("aiUsage.fiveHour", 11, 300, now.AddSeconds(-1))]), now);
        var root = Assert.IsType<Grid>(view.Content);
        foreach (var (provider, text, fill) in new[] { ("Codex", "89% left", 89.0), ("Claude", "—", 0.0) })
        {
            var pane = root.Children.OfType<Grid>().Single(p => p.Name == provider + "NotchPane");
            var body = Assert.IsType<StackPanel>(pane.Children[1]);
            var row = Assert.IsType<StackPanel>(body.Children[0]);
            var line = Assert.IsType<Grid>(row.Children[0]);
            Assert.Equal(text, Assert.IsType<TextBlock>(line.Children[2]).Text);
            var track = Assert.IsType<Border>(row.Children[1]);
            Assert.Equal(fill, Assert.IsType<Grid>(track.Child).ColumnDefinitions[0].Width.Value);
        }
    });

    [Fact]
    public void StatWidgetKeepsCodexBucketsTogetherAndDoesNotInventAWeeklyWindow() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var widget = new AiUsageStatWidget();
        widget.Present(new("Codex", AiUsageState.Ready, now,
            [new("aiUsage.primary", 47, 15, now.AddMinutes(10), "codex"),
             new("aiUsage.primary", 0, 300, now.AddHours(2), "other"),
             new("aiUsage.secondary", 0, 10080, now.AddDays(2), "other")], "plus", "user@example.test"),
            new("Claude", AiUsageState.Waiting), now, default(ClaudeBridgeSetupStatus));
        var root = Assert.IsType<StackPanel>(widget.Content);
        var codex = Assert.IsType<StackPanel>(root.Children[0]);
        Assert.Equal("53% left", AiUsageWidgetTestUi.Text(codex, "PrimaryAllowance"));
        Assert.Equal("Plus", AiUsageWidgetTestUi.Text(codex, "SubscriptionPlan"));
        Assert.Equal("15 min", AiUsageWidgetTestUi.Text(codex, "PrimaryWindowLabel"));
        Assert.Equal("—", AiUsageWidgetTestUi.Text(codex, "WeeklyAllowance"));
        string tooltip = Assert.IsType<string>(codex.ToolTip);
        Assert.Contains("user@example.test", tooltip);
        Assert.Contains("47% Used", tooltip);
        Assert.Contains("Updated", tooltip);
        Assert.Contains("Resets", tooltip);
    });

    [Fact]
    public void StatWidgetExpiredTooltipDoesNotInventZeroUsageOrFullAllowance() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var widget = new AiUsageStatWidget();
        widget.Present(new("Codex", AiUsageState.Ready, now,
            [new("aiUsage.primary", 73, 300, now.AddSeconds(-1))]), new("Claude", AiUsageState.Waiting), now);
        var root = Assert.IsType<StackPanel>(widget.Content);
        var codex = Assert.IsType<StackPanel>(root.Children[0]);
        string tooltip = Assert.IsType<string>(codex.ToolTip);
        Assert.Contains(Loc.Get("aiUsage.expired"), tooltip);
        Assert.DoesNotContain("0% Used", tooltip);
        Assert.DoesNotContain("100% left", tooltip);
    });

    [Fact]
    public void StatWidgetFitsTheMainViewColumnInEveryLocale() => SharedStaTestRunner.Run(() =>
    {
        try
        {
            foreach (var (language, _) in Loc.GetAvailableLanguages())
            {
                Loc.SetLanguage(language);
                var widget = new AiUsageStatWidget();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                widget.Present(new("Codex", AiUsageState.Ready, now,
                    [new("aiUsage.primary", 25, 300, now.AddHours(2)), new("aiUsage.secondary", 81, 10080, now.AddDays(2))]),
                    new("Claude", AiUsageState.Ready, now,
                    [new("aiUsage.fiveHour", 40, 300, now.AddHours(1)), new("aiUsage.sevenDay", 60, 10080, now.AddDays(3))]), now);
                widget.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(widget.DesiredSize.Width <= 140, language);
                Assert.True(widget.DesiredSize.Height <= 110, language);
            }
        }
        finally { Loc.SetLanguage("en"); }
    });

    [Fact]
    public void QuotasRenderWithinTheNotchInEveryLocaleAndAtHighDpi() => SharedStaTestRunner.Run(() =>
    {
        try
        {
            foreach (var (language, _) in Loc.GetAvailableLanguages())
            {
                Loc.SetLanguage(language);
                var widget = new AiAccountUsageWidget { Compact = true };
                widget.Resources["AccentBrushStrong"] = Brushes.LightGreen;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                widget.Present(new("Codex", AiUsageState.Ready, now,
                    [new("aiUsage.primary", 25, 300, now.AddHours(2)), new("aiUsage.secondary", 81, 10080, now.AddDays(2))]),
                    new("Claude", AiUsageState.Ready, now,
                    [new("aiUsage.fiveHour", 40, 300, now.AddHours(1)), new("aiUsage.sevenDay", 60, 10080, now.AddDays(3))]));
                widget.Measure(new Size(132, 84));
                Assert.True(widget.DesiredSize.Width <= 132, language);
                Assert.True(widget.DesiredSize.Height <= 84, language);
                widget.Arrange(new Rect(0, 0, 132, 84));
                widget.UpdateLayout();
                Assert.Equal(Loc.Get("aiUsage.title"), AutomationProperties.GetName(widget));
                var bitmap = new RenderTargetBitmap(264, 168, 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(widget);
                byte[] pixels = new byte[264 * 168 * 4];
                bitmap.CopyPixels(pixels, 264 * 4, 0);
                Assert.True(pixels.Count(b => b != 0) > 100, language);
                if (language == "en")
                {
                    string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "ai-usage");
                    Directory.CreateDirectory(directory);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, "widget-200dpi.png"));
                    encoder.Save(file);
                }
            }
        }
        finally { Loc.SetLanguage("en"); }
    });

    [Fact]
    public void ExpiredWindowRemovesTheRemainingBarAndIncludesResetExplanation() => SharedStaTestRunner.Run(() =>
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var card = AiAccountUsageWidget.CreateProviderCard(new("Claude", AiUsageState.Ready, now.AddMinutes(-20),
            [new("aiUsage.fiveHour", 85, 300, now.AddMinutes(-1))]));
        card.Measure(new Size(336, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, 336, card.DesiredSize.Height));
        card.UpdateLayout();
        var panel = Assert.IsType<StackPanel>(card.Child);
        var row = Assert.IsType<StackPanel>(panel.Children[1]);
        Assert.Equal("—", Assert.IsType<TextBlock>(row.Children[2]).Text);
        var track = Assert.IsType<Border>(row.Children[1]);
        Assert.Equal(0, Assert.IsType<Grid>(track.Child).ColumnDefinitions[0].Width.Value);
        Assert.Contains(Loc.Get("aiUsage.expired"), card.ToolTip!.ToString());
    });

    [Fact]
    public void ReferenceCardsShowRemainingPercentagesResetTimesAndDistinctBundledLogos() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTimeOffset now = DateTimeOffset.Now;
        foreach (string provider in new[] { "Claude", "Codex" })
        {
            var card = AiAccountUsageWidget.CreateProviderCard(new(provider, AiUsageState.Ready, now,
                [new("aiUsage.primary", 73, 300, now.AddMinutes(51)), new("aiUsage.secondary", 7, 10080, now.AddDays(2))]), observedNow: now);
            card.Measure(new Size(352, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 352, card.DesiredSize.Height));
            card.UpdateLayout();
            var panel = Assert.IsType<StackPanel>(card.Child);
            var header = Assert.IsType<StackPanel>(panel.Children[0]);
            var logo = Assert.IsType<Image>(header.Children[0]);
            Assert.Same(AiProviderBrand.For(provider), logo.Source);
            Assert.Equal(provider + " Usage", Assert.IsType<TextBlock>(header.Children[1]).Text);
            var first = Assert.IsType<StackPanel>(panel.Children[1]);
            Assert.Equal("27% left", Assert.IsType<TextBlock>(first.Children[2]).Text);
            var line = Assert.IsType<Grid>(first.Children[0]);
            Assert.Equal("Resets in 51 min", Assert.IsType<TextBlock>(line.Children[1]).Text);
            var track = Assert.IsType<Border>(first.Children[1]);
            var fillGrid = Assert.IsType<Grid>(track.Child);
            Assert.Equal(27, fillGrid.ColumnDefinitions[0].Width.Value);
            Assert.Equal(Color.FromRgb(255, 77, 8), Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(fillGrid.Children[0]).Background).Color);
            var secondary = Assert.IsType<StackPanel>(panel.Children[2]);
            Assert.Equal("93% left", Assert.IsType<TextBlock>(secondary.Children[2]).Text);
            SaveCard(card, provider.ToLowerInvariant() + "-usage-card.png");
        }
        Assert.NotSame(AiProviderBrand.For("Claude"), AiProviderBrand.For("Codex"));
    });

    private static void SaveCard(FrameworkElement element, string name)
    {
        int width = (int)Math.Ceiling(element.ActualWidth * 2);
        int height = (int)Math.Ceiling(element.ActualHeight * 2);
        var bitmap = new RenderTargetBitmap(width, height, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "ai-usage");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    [Fact]
    public void AiUsageNotchViewRendersMainViewLayoutAndNativeVisualsAcrossAllLocales() => SharedStaTestRunner.Run(() =>
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, VNotchNative.vn_ai_usage_compute_window_visual(
            75.0, 300, 1, now.AddMinutes(42).UtcDateTime.Ticks, 1, now.UtcDateTime.Ticks, now.UtcDateTime.Ticks, out var visual));
        Assert.Equal((byte)1, visual.SeverityTier);
        Assert.Equal((byte)2, visual.ResetMode);
        Assert.Equal(42, visual.ResetMinutes);
        Assert.Equal(0, VNotchNative.vn_ai_usage_compute_card_summary((int)AiUsageState.Disabled, 0, 0, 0, now.UtcDateTime.Ticks, out var disabledSummary));
        Assert.Equal((byte)0, disabledSummary.ShowQuotaRows);
        Assert.Equal((byte)1, disabledSummary.ActionKind);

        try
        {
            foreach (var (language, _) in Loc.GetAvailableLanguages())
            {
                Loc.SetLanguage(language);
                var view = new AiUsageNotchView();
                view.Present(
                    new("Codex", AiUsageState.Ready, now,
                        [new("aiUsage.primary", 25, 300, now.AddHours(2)), new("aiUsage.secondary", 81, 10080, now.AddDays(2))],
                        Plan: "Plus"),
                    new("Claude", AiUsageState.Ready, now,
                        [new("aiUsage.fiveHour", 40, 300, now.AddHours(1)), new("aiUsage.sevenDay", 92, 10080, now.AddDays(3))],
                        Plan: "Pro"),
                    observedNow: now);

                view.Measure(new Size(464, 137));
                Assert.True(view.DesiredSize.Width <= 464, language);
                Assert.True(view.DesiredSize.Height <= 137, language);
                view.Arrange(new Rect(0, 0, 464, 137));
                view.UpdateLayout();
                Assert.Equal(Loc.Get("aiUsage.title"), AutomationProperties.GetName(view));
                Assert.False(string.IsNullOrWhiteSpace(Loc.Get("aiUsage.claudeNotConnected")), language);
                Assert.False(string.IsNullOrWhiteSpace(Loc.Get("aiUsage.statusSummary")), language);
            }
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    });

    [Fact]
    public void AiUsageWidgetModeHidesCalendarAndGreetingChrome() => SharedStaTestRunner.Run(() =>
    {
        var calendarStrip = new Border();
        var greeting = new StackPanel();
        var calendarWidget = new Grid();
        var aiusageView = new AiUsageStatWidget();
        var host = new Host();
        host.Settings.ExpandedWidget = "aiusage";
        using var presenter = new ClockWidgetPresenter(host, new ClockWidgetViewRefs
        {
            Window = host.Window,
            NotchBorder = new Border(),
            CalendarWidget = calendarWidget,
            CalendarStripContainer = calendarStrip,
            GreetingSection = greeting,
            ClockWidget = new Border(),
            AiUsageWidgetContent = aiusageView
        });

        presenter.ApplyExpandedWidgetMode();
        Assert.Equal(Visibility.Visible, aiusageView.Visibility);
        Assert.Equal(Visibility.Collapsed, calendarStrip.Visibility);
        Assert.Equal(Visibility.Collapsed, greeting.Visibility);
        Assert.False(string.IsNullOrWhiteSpace(((FrameworkElement)((StackPanel)aiusageView.Content).Children[0]).ToolTip?.ToString()));
    });

    private sealed class Host : IClockWidgetHost
    {
        internal int AiUsageOpens;
        internal int AiUsageSettingsOpens;
        public NotchSettings Settings { get; } = new();
        public void SaveSettings() { }
        public bool IsAnimating { get; set; }
        public bool IsSecondaryView => false;
        public bool IsExpanded => true;
        public bool IsTimerView => false;
        public bool IsLyricsActive => false;
        public int TransitionGeneration => 0;
        public Brush TransparentBrush => Brushes.Transparent;
        public Brush WhiteBrush => Brushes.White;
        public void SwitchToTimerView() { }
        public void SwitchToAiUsageView() => AiUsageOpens++;
        public void OpenAiUsageSettings() => AiUsageSettingsOpens++;
        public void CollapseNotch() { }
        public IntPtr Hwnd => IntPtr.Zero;
        public int FixedX => 0;
        public int FixedY => 0;
        public int WindowWidth => 600;
        public int WindowHeight { get; set; } = 300;
        public double ExpandedHeight => 154;
        public double MaxExpandedHeight => 400;
        public Window Window { get; } = new();
    }
}
