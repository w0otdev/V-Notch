using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AiSubscriptionCustomizationTests
{
    [Fact]
    public void DensityChangesTheRenderedSizeAndPlanVisibilityChangesVisibleText() => SharedStaTestRunner.RunAsync(async () =>
    {
        var appearance = AiUsageAppearance.Current;
        var widget = new AiUsageStatWidget { IsPreview = true, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        var host = new Window { Content = widget, Width = 160, Height = 160, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        BackgroundTestWindows.ProtectInput(host);
        try
        {
            AiUsageAppearance.Apply(new AiUsageAppearance());
            host.Show();
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            double comfortableHeight = widget.ActualHeight;
            var root = Assert.IsType<StackPanel>(widget.Content);
            var row = Assert.IsType<StackPanel>(root.Children[0]);
            Assert.Contains("Plus", AiUsageWidgetTestUi.Text(row, "SubscriptionPlan"));

            AiUsageAppearance.Apply(AiUsageAppearance.Current with { Compact = true, ShowPlan = false });
            await host.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(widget.ActualHeight <= comfortableHeight - 10,
                $"Comfortable: {comfortableHeight}; compact: {widget.ActualHeight}");
            row = Assert.IsType<StackPanel>(root.Children[0]);
            Assert.DoesNotContain("Plus", AiUsageWidgetTestUi.Text(row, "SubscriptionPlan"));
        }
        finally { host.Close(); AiUsageAppearance.Apply(appearance); }
    });

    [Fact]
    public void HidingWeeklyAllowanceAlsoHidesAWeeklyOnlySample() => SharedStaTestRunner.Run(() =>
    {
        var appearance = AiUsageAppearance.Current;
        try
        {
            AiUsageAppearance.Apply(new AiUsageAppearance { ShowWeekly = false });
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var widget = new AiUsageStatWidget();
            widget.Present(new("Codex", AiUsageState.Ready, now,
                [new("aiUsage.secondary", 2, 10080, now.AddDays(2))]), new("Claude", AiUsageState.Waiting), now);
            var root = Assert.IsType<StackPanel>(widget.Content);
            var row = Assert.IsType<StackPanel>(root.Children[0]);
            string summary = AiUsageWidgetTestUi.Text(row, "SubscriptionPlan");
            Assert.DoesNotContain("98%", summary);
            Assert.DoesNotContain("7d", summary);
        }
        finally { AiUsageAppearance.Apply(appearance); }
    });

    [Fact]
    public void PreferencesRoundTripCloneAndValidateValues()
    {
        var settings = new NotchSettings
        {
            AiUsageShowCodex = false, AiUsageClaudeFirst = true, AiUsageCompact = true,
            AiUsageShowUsedPercent = true, AiUsageShowWeekly = false, AiUsageShowResets = false,
            AiUsageShowPlan = false, AiUsageShowLogos = false, AiUsageShowAccount = false,
            AiUsageStatusColors = false, AiUsageCodexColor = "#abcdef", AiUsageClaudeColor = "#326BFF",
            AiUsageBarHeight = 9, AiUsageMotionIntensity = 35
        };
        var clone = settings.Clone();
        Assert.True(settings.ValueEquals(clone));
        var restored = JsonSerializer.Deserialize<NotchSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(settings.ValueEquals(restored));
        Assert.Equal("#ABCDEF", restored.AiUsageCodexColor);
        clone.AiUsageMotionIntensity = 65;
        Assert.False(settings.ValueEquals(clone));
        var invalid = JsonSerializer.Deserialize<NotchSettings>("""{"AiUsageBarHeight":999,"AiUsageMotionIntensity":-1,"AiUsageClaudeColor":"not a color"}""")!;
        Assert.Equal(10, invalid.AiUsageBarHeight);
        Assert.Equal(0, invalid.AiUsageMotionIntensity);
        Assert.Equal("#D97757", invalid.AiUsageClaudeColor);
    }

    [Fact]
    public void CustomizationControlsBothWidgetAndDetailsWithoutExposingHiddenFields() => SharedStaTestRunner.Run(() =>
    {
        var original = AiUsageAppearance.Current;
        try
        {
            Loc.SetLanguage("en");
            AiUsageAppearance.Apply(new NotchSettings
            {
                AiUsageShowCodex = false, AiUsageClaudeFirst = true, AiUsageShowUsedPercent = true,
                AiUsageShowWeekly = false, AiUsageShowResets = false, AiUsageShowLogos = false,
                AiUsageShowAccount = false, AiUsageShowPlan = false, AiUsageStatusColors = false,
                AiUsageClaudeColor = "#326BFF", AiUsageBarHeight = 9, AiUsageMotionIntensity = 0
            });
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var claude = new AiAccountUsage("Claude", AiUsageState.Ready, now,
                [new("aiUsage.fiveHour", 37, 300, now.AddHours(2)), new("aiUsage.sevenDay", 21, 10080, now.AddDays(2))], "private-plan", "private-account");
            var widget = new AiUsageStatWidget();
            widget.Present(new("Codex", AiUsageState.Ready), claude, now);
            widget.Measure(new Size(130, 110));
            widget.Arrange(new Rect(0, 0, 130, 110));
            widget.UpdateLayout();
            var root = Assert.IsType<StackPanel>(widget.Content);
            var block = Assert.IsType<StackPanel>(Assert.Single(root.Children.Cast<UIElement>()));
            Assert.Equal("ClaudeSubscription", block.Name);
            var header = Assert.IsType<Grid>(block.Children[0]);
            Assert.Equal("37% Used", Assert.IsType<TextBlock>(header.Children[1]).Text);
            Assert.Empty(Assert.IsType<StackPanel>(header.Children[0]).Children.OfType<Image>());
            var track = Assert.IsType<Border>(block.Children[1]);
            Assert.Equal(9, track.Height);
            var fill = Assert.IsType<Border>(track.Child);
            Assert.InRange(Math.Abs(track.ActualWidth * .37 - fill.ActualWidth),
                0, 1 / VisualTreeHelper.GetDpi(fill).DpiScaleX);
            Assert.Equal(Color.FromRgb(0x32, 0x6B, 0xFF), Assert.IsType<SolidColorBrush>(fill.Background).Color);
            Assert.Equal("5h", AiUsageWidgetTestUi.Text(block, "SubscriptionPlan"));
            string tooltip = Assert.IsType<string>(block.ToolTip);
            Assert.DoesNotContain("private-", tooltip);
            Assert.DoesNotContain("Resets", tooltip);
            Assert.DoesNotContain("7 days", tooltip);

            var details = new AiUsageNotchView();
            details.Present(new("Codex", AiUsageState.Ready), claude, now);
            var detailRoot = Assert.IsType<Grid>(details.Content);
            var pane = Assert.IsType<Grid>(Assert.Single(detailRoot.Children.Cast<UIElement>()));
            Assert.Equal("ClaudeNotchPane", pane.Name);
            var body = Assert.IsType<StackPanel>(pane.Children[1]);
            var row = Assert.Single(body.Children.OfType<StackPanel>());
            var line = Assert.IsType<Grid>(row.Children[0]);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(line.Children[1]).Visibility);
            Assert.Equal("37% Used", Assert.IsType<TextBlock>(line.Children[2]).Text);
        }
        finally { AiUsageAppearance.Apply(original); }
    });

    [Fact]
    public void AllHiddenProvidersKeepAnActionableEmptyState() => SharedStaTestRunner.Run(() =>
    {
        var original = AiUsageAppearance.Current;
        try
        {
            AiUsageAppearance.Apply(new NotchSettings { AiUsageShowCodex = false, AiUsageShowClaude = false });
            var widget = new AiUsageStatWidget();
            var root = Assert.IsType<StackPanel>(widget.Content);
            Assert.Equal(Loc.Get("aiUsage.chooseProviders"), Assert.IsType<TextBlock>(Assert.Single(root.Children.Cast<UIElement>())).Text);
        }
        finally { AiUsageAppearance.Apply(original); }
    });

    [Fact]
    public void ActiveBarMotionSnapsToItsTargetWhenReducedMotionIsEnabled() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var appearance = AiUsageAppearance.Current;
        bool reduced = AnimationConfig.ReduceMotion;
        bool? clientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        var owner = new Border { Width = 130, Height = 20 };
        var motion = new AiUsageMotion(owner);
        using var host = new HwndSource(new HwndSourceParameters("AI motion regression") { Width = 150, Height = 40, WindowStyle = 0 }) { RootVisual = owner };
        host.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        try
        {
            AiUsageAppearance.Apply(new AiUsageAppearance());
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(false);
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var fill = new Border { Background = Brushes.Green, Width = 130, Height = 5 };
            owner.Child = fill;
            motion.BeginPresentation(false);
            motion.BindBar(fill, "codex", 75, true);
            if (!SystemParameters.HighContrast)
            {
                Assert.True(fill.HasAnimatedProperties);
                await WpfFrameWaiter.UntilAsync(() => fill.ActualWidth > 0 && fill.ActualWidth < 129, "quota begins draining", ct);
            }
            motion.BeginPresentation(false);
            var replacement = new Border { Width = 130, Height = 5 };
            owner.Child = replacement;
            motion.BindBar(replacement, "codex", 25, true);
            AnimationConfig.SetReduceMotion(true);
            owner.UpdateLayout();
            Assert.False(replacement.HasAnimatedProperties);
            Assert.Equal(owner.ActualWidth * .25, replacement.ActualWidth, 6);
            owner.Visibility = Visibility.Collapsed;
            Assert.False(replacement.HasAnimatedProperties);
        }
        finally
        {
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            AnimationConfig.ClientAreaAnimationOverride = clientAnimation;
            AnimationConfig.SetReduceMotion(reduced);
            AiUsageAppearance.Apply(appearance);
        }
    });

    [Fact]
    public void TurningOffWidgetMotionStopsAnInFlightRefreshSpinner() => SharedStaTestRunner.RunAsync(async () =>
    {
        var appearance = AiUsageAppearance.Current;
        bool reduced = AnimationConfig.ReduceMotion;
        bool? clientAnimation = AnimationConfig.ClientAreaAnimationOverride;
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L10,0 L10,10"), Width = 10, Height = 10 };
        var button = new ActionBorder { Child = icon, Width = 28, Height = 28 };
        var owner = new Border { Child = button };
        var motion = new AiUsageMotion(owner);
        using var host = new HwndSource(new HwndSourceParameters("AI spinner regression") { Width = 50, Height = 50, WindowStyle = 0 }) { RootVisual = owner };
        try
        {
            AiUsageAppearance.Apply(new AiUsageAppearance());
            AnimationConfig.ClientAreaAnimationOverride = true;
            AnimationConfig.SetReduceMotion(false);
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            using var busy = motion.Busy(button);
            if (!SystemParameters.HighContrast) Assert.True(Assert.IsType<RotateTransform>(icon.RenderTransform).HasAnimatedProperties);
            AiUsageAppearance.Apply(AiUsageAppearance.Current with { MotionIntensity = 0 });
            if (icon.RenderTransform is RotateTransform rotation)
            {
                Assert.False(rotation.HasAnimatedProperties);
                Assert.Equal(0, rotation.Angle);
            }
        }
        finally
        {
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            AnimationConfig.ClientAreaAnimationOverride = clientAnimation;
            AnimationConfig.SetReduceMotion(reduced);
            AiUsageAppearance.Apply(appearance);
        }
    });
}
