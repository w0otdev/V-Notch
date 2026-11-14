using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class NotchNavigationIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AiViewMeasuresCurrentQuotasBeforeItsFirstVisibleFrame(bool fromMedia, bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var monitor = Services.AiUsage.AiAccountUsageMonitor.Shared;
        var originalCodex = monitor.Codex;
        var originalClaude = monitor.Claude;
        bool reduced = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.EnableAiUsageMonitoring = false;
                settings.DisableMouseLeaveAutoClose = true;
                settings.EnableDynamicIslandMode = island;
                settings.AiUsageMotionIntensity = 0;
            }, services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        void SetSnapshot(string provider, AiAccountUsage snapshot) => monitor.GetType()
            .GetProperty(provider, Private)!.SetValue(monitor, snapshot);
        try
        {
            AnimationConfig.SetReduceMotion(false);
            await StartAsync(window, ct);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            if (fromMedia)
            {
                Assert.True(coordinator.RequestView(NotchView.Media, "AiPreflightSource"));
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window), "media source", ct);
            }
            Assert.False(window.AiUsageViewControl.IsVisible);
            // Simulate the snapshot arriving while the AI tab is hidden.
            window.AiUsageViewControl.Present(new("Codex", AiUsageState.Loading), new("Claude", AiUsageState.Disabled));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var codex = new AiAccountUsage("Codex", AiUsageState.Ready, now,
                [new("aiUsage.primary", 90, 300, now.AddMinutes(137)), new("aiUsage.secondary", 33, 10080, now.AddDays(3))], "plus");
            var claude = new AiAccountUsage("Claude", AiUsageState.Disabled);
            SetSnapshot("Codex", codex);
            SetSnapshot("Claude", claude);
            var expected = new Controls.AiUsageNotchView();
            expected.Present(codex, claude, now);
            expected.Measure(new Size(464, double.PositiveInfinity));
            double outerMargin = window.AiUsageContent.Margin.Top + window.AiUsageContent.Margin.Bottom;
            double expectedHeight = Math.Ceiling(Math.Max(Property<double>(window, "_expandedHeight"), expected.DesiredSize.Height + outerMargin));

            Assert.True(coordinator.RequestView(NotchView.AiUsage, "AiPreflight"));
            ExecutePendingTransition(window);
            Assert.Equal(expectedHeight, window.AiUsageContent.Height + outerMargin, precision: 2);
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window), "AI expansion", ct);
            Assert.Equal(expectedHeight, window.NotchBorder.Height, precision: 2);
            for (int i = 0; i < 3; i++)
            {
                await WpfFrameWaiter.NextAsync(ct);
                Assert.Equal(expectedHeight, window.NotchBorder.Height, precision: 2);
            }
        }
        finally
        {
            SetSnapshot("Codex", originalCodex);
            SetSnapshot("Claude", originalClaude);
            AnimationConfig.SetReduceMotion(reduced);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryExpandedViewCanNavigateToEveryOtherViewAndCollapse(bool blurEnabled, bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.EnableBlurEffects = blurEnabled;
                settings.EnableDynamicIslandMode = island;
            },
            services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        using var mixer = new AudioMixerService();
        typeof(Controls.AudioMixerView).GetField("_audioMixerServiceCached", Private)!.SetValue(window.AudioContent, mixer);
        typeof(Controls.AudioMixerView).GetField("_masterVolumeCached", Private)!.SetValue(window.AudioContent, new Fakes.FakeVolumeService());
        try
        {
            await StartAsync(window, ct);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            Assert.False(window.SettingsButton.IsHitTestVisible);
            var views = new[] { NotchView.Media, NotchView.Secondary, NotchView.Timer, NotchView.AudioMixer, NotchView.Camera, NotchView.AiUsage };
            bool capturedHeader = false;
            async Task Navigate(NotchView view)
            {
                if (coordinator.CurrentView != view)
                {
                    if (view == NotchView.Compact) window.SetDebugViewLock(false);
                    Assert.True(coordinator.RequestView(view, "NavigationMatrix"), $"Navigation from {coordinator.CurrentView} to {view} was rejected.");
                    window.SetDebugViewLock(true);
                    await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window),
                        $"navigation to {view}", ct);
                }
                Assert.Equal(view, coordinator.CurrentView);
                Assert.Equal(view, ((ShellViewModel)window.DataContext).CurrentView);
                Assert.Equal(view != NotchView.Compact, Property<bool>(window, "_isExpanded"));
                Assert.True(window.NotchBorder.IsHitTestVisible);
                if (view != NotchView.Compact)
                {
                    await WpfFrameWaiter.UntilAsync(() => window.SettingsButton.Opacity > 0.99
                        && Math.Abs(window.SettingsRotate.Angle - 45) < 1e-6,
                        "shared Settings button revealed", ct);
                    window.UpdateLayout();
                    Assert.True(window.SettingsButton.IsVisible);
                    Assert.True(window.SettingsButton.IsHitTestVisible);
                    var bounds = window.SettingsButton.TransformToAncestor(window.NotchContent)
                        .TransformBounds(new Rect(window.SettingsButton.RenderSize));
                    Assert.True(bounds.Right <= window.NotchContent.ActualWidth + 1 && bounds.Left >= 0,
                        $"Settings button must stay inside the {view} view.");
                    var hit = window.NotchContent.InputHitTest(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)) as DependencyObject;
                    while (hit != null && !ReferenceEquals(hit, window.SettingsButton)) hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(window.SettingsButton, hit);
                    if (view == NotchView.Secondary)
                    {
                        var tray = window.ClipboardTrayView;
                        Assert.Same(window.SettingsButton, tray.SettingsButtonHost.Content);
                        var settingsBounds = window.SettingsButton.TransformToAncestor(tray)
                            .TransformBounds(new Rect(window.SettingsButton.RenderSize));
                        var trashBounds = tray.EmptyTrayButton.TransformToAncestor(tray)
                            .TransformBounds(new Rect(tray.EmptyTrayButton.RenderSize));
                        Assert.Equal(trashBounds.Top + trashBounds.Height / 2,
                            settingsBounds.Top + settingsBounds.Height / 2, precision: 2);
                        Assert.Equal(32, settingsBounds.Left + settingsBounds.Width / 2
                            - trashBounds.Left - trashBounds.Width / 2, precision: 2);
                        Assert.True(settingsBounds.Left >= trashBounds.Right + 2,
                            $"Toolbar hit areas overlap: trash={trashBounds}, settings={settingsBounds}");
                        if (!capturedHeader && Environment.GetEnvironmentVariable("VNOTCH_SETTINGS_ICON_PREVIEW_DIR") is { Length: > 0 } previewDirectory)
                        {
                            System.IO.Directory.CreateDirectory(previewDirectory);
                            var drawing = new DrawingVisual();
                            using (var context = drawing.RenderOpen())
                                context.DrawRectangle(new VisualBrush(tray)
                                {
                                    ViewboxUnits = BrushMappingMode.Absolute,
                                    Viewbox = new Rect(0, 0, tray.ActualWidth, 34)
                                }, null, new Rect(0, 0, tray.ActualWidth, 34));
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)Math.Ceiling(tray.ActualWidth * 1.25), 43, 120, 120, PixelFormats.Pbgra32);
                            bitmap.Render(drawing);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                            using var file = System.IO.File.Create(System.IO.Path.Combine(previewDirectory, $"tray-header-{island}-{blurEnabled}.png"));
                            encoder.Save(file);
                            capturedHeader = true;
                        }
                    }
                }
                else
                {
                    // A late MouseLeave must not resurrect the shared icon.
                    typeof(MainWindow).GetMethod("SettingsButton_MouseLeave", Private)!.Invoke(window,
                        [window, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)]);
                    Assert.False(window.SettingsButton.IsHitTestVisible);
                    Assert.Equal(Visibility.Collapsed, window.SettingsButton.Visibility);
                    Assert.Equal(0, window.SettingsButton.Opacity);
                }
            }
            int pairIndex = 0;
            foreach (var from in views)
            {
                foreach (var to in views.Where(view => view != from))
                {
                    // Exercise full storyboard clocks for the first cycle, then fast-forward remaining permutations.
                    AnimationConfig.SetReduceMotion(pairIndex++ >= 2);
                    await Navigate(from);
                    await Navigate(to);
                    FrameworkElement content = to switch
                    {
                        NotchView.Media => window.ExpandedContent,
                        NotchView.Secondary => window.SecondaryContent,
                        NotchView.Timer => window.TimerContent,
                        NotchView.Camera => window.CameraContent,
                        NotchView.AiUsage => window.AiUsageContent,
                        _ => window.AudioContent
                    };
                    Assert.Equal(Visibility.Visible, content.Visibility);
                    if (to == NotchView.AiUsage)
                    {
                        Assert.Same(window.StatusBar, window.AiUsageStatusBarHost.Content);
                        Assert.Equal(480, window.NotchBorder.ActualWidth, precision: 1);
                    }
                }
                AnimationConfig.SetReduceMotion(pairIndex > 4);
                await Navigate(NotchView.Compact);
            }
            Assert.Equal(NotchShapeState.Collapsed, coordinator.ShapeState);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(previousReducedMotion);
        }
    }, timeoutSeconds: 90);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraySearchAndActionsShareTheNavigationBaseline(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.EnableDynamicIslandMode = island;
            }, services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        await StartAsync(window, ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.True(coordinator.RequestView(NotchView.Secondary, "HeaderAlignment"));
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window)
            && Math.Abs(window.SettingsRotate.Angle - 45) < 1e-6, "tray header settled", ct);
        window.UpdateLayout();
        double CenterY(FrameworkElement element)
        {
            var bounds = element.TransformToAncestor(window.NotchContent).TransformBounds(new Rect(element.RenderSize));
            return bounds.Top + bounds.Height / 2;
        }
        double navigationCenter = CenterY(window.HomeIconButton);
        var tray = window.ClipboardTrayView;
        foreach (var element in new FrameworkElement[] { tray.SearchSurface, tray.AddLabelButton,
            tray.SelectAllButton, tray.EmptyTrayButton, window.SettingsButton })
        {
            double difference = CenterY(element) - navigationCenter;
            Assert.True(Math.Abs(difference) <= 0.5,
                $"{element.Name} is offset from navigation by {difference:F2} DIPs (island={island}).");
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BatteryChargingAndSettingsShareTheNavigationBaseline(bool island, bool update) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.ShowBatteryIndicator = true;
                settings.EnableDynamicIslandMode = island;
            }, services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
        var window = fixture.Window;
        await StartAsync(window, ct);
        typeof(MainWindow).GetField("_isUpdateAvailable", Private)!.SetValue(window, update);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.True(coordinator.RequestView(NotchView.Media, "StatusAlignment"));
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window)
            && Math.Abs(window.SettingsRotate.Angle - 45) < 1e-6, "status header settled", ct);
        window.BatteryPercent.Text = "100%";
        window.ChargingBolt.Visibility = Visibility.Visible;
        window.UpdateLayout();
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window.NotchContent)
            .TransformBounds(new Rect(element.RenderSize));
        var nav = Bounds(window.HomeIconButton);
        double center = nav.Top + nav.Height / 2;
        foreach (var element in new FrameworkElement[] { window.BatteryIcon, window.ChargingBolt, window.SettingsButton })
        {
            var bounds = Bounds(element);
            double difference = bounds.Top + bounds.Height / 2 - center;
            Assert.True(Math.Abs(difference) <= 0.5,
                $"{element.Name} is offset from navigation by {difference:F2} DIPs (island={island}, update={update}).");
        }
        if (update)
        {
            var bounds = Bounds(window.UpdateNotificationButton);
            Assert.InRange(Math.Abs(bounds.Top + bounds.Height / 2 - center), 0, 0.5);
            Assert.True(bounds.Left >= Bounds(window.BatteryIcon).Right + 3.5);
            Assert.True(Bounds(window.SettingsButton).Left >= bounds.Right + 3.5);
        }
        else Assert.True(Bounds(window.SettingsButton).Left >= Bounds(window.BatteryIcon).Right + 3.5);
    });

    [Fact]
    public void InterruptedAnimationsSettleInTheLatestViewAcrossAllConsumers() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.Same(coordinator, Field<NotchManager>(window, "_notchManager").TransitionCoordinator);
        for (int i = 0; i < 12; i++)
        {
            window.SetDebugViewState("MediaExpanded");
            await WpfFrameWaiter.NextAsync(ct);
            window.SetDebugViewState("CollapsedNotch");
            await WpfFrameWaiter.NextAsync(ct);
            window.SetDebugViewState(i % 2 == 0 ? "SecondaryShelf" : "TimerStopwatch");
            await WpfFrameWaiter.NextAsync(ct);
        }
        window.SetDebugViewState("SecondaryShelf");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "the final shelf transition", ct);
        Assert.Equal(NotchView.Secondary, coordinator.CurrentView);
        Assert.Equal(NotchShapeState.Expanded, coordinator.ShapeState);
        Assert.Equal(NotchView.Secondary, ((ShellViewModel)window.DataContext).CurrentView);
        Assert.True(Property<bool>(window, "_isExpanded"));
        Assert.True(Property<bool>(window, "_isSecondaryView"));
        Assert.Equal(Visibility.Visible, window.SecondaryContent.Visibility);

        window.SetDebugViewState("CollapsedNotch");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "collapse after reversal", ct);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.Compact, ((ShellViewModel)window.DataContext).CurrentView);
        Assert.False(Property<bool>(window, "_isExpanded"));
        Assert.False(Property<bool>(window, "_isSecondaryView"));
    });

    [Fact]
    public void ReopeningMediaDuringCollapseRestoresTheExpandedShell() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "initial media expansion", ct);
        double expandedWidth = window.NotchBorder.Width;
        window.SetDebugViewState("CollapsedNotch");
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width < expandedWidth - 10, "partway through collapse", ct);
        double opacity = window.ExpandedContent.Opacity;
        double y = window.ExpandedContent.RenderTransform.Value.OffsetY;
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        Assert.True(coordinator.RequestView(NotchView.Media, "ReverseCollapse"));
        ExecutePendingTransition(window);
        Assert.Equal(opacity, window.ExpandedContent.Opacity, 6);
        Assert.Equal(y, window.ExpandedContent.RenderTransform.Value.OffsetY, 6);
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "reversed media expansion", ct);
        Assert.Equal(NotchView.Media, coordinator.CurrentView);
        Assert.Equal(expandedWidth, window.NotchBorder.Width, 2);
        Assert.Equal(Visibility.Visible, window.ExpandedContent.Visibility);
        Assert.Equal(1, window.ExpandedContent.Opacity, 2);
    });

    [Fact]
    public void CountdownDismissCompletesTheCoordinatorAndAllowsReopening() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        coordinator.NotifyCountdownCompleted();
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive && !IsAnimating(window), "countdown completion", ct);
        Assert.Equal(NotchView.Timer, coordinator.CurrentView);

        window.SetDebugViewLock(false);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };
        typeof(MainWindow).GetMethod("CountdownDismiss_Click", Private)!.Invoke(window, [window, click]);
        window.SetDebugViewLock(true);
        Assert.Equal(NotchView.Compact, coordinator.TargetView);
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "countdown dismissal", ct);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
        Assert.Equal(NotchView.Compact, ((ShellViewModel)window.DataContext).CurrentView);
        window.SetDebugViewState("TimerStopwatch");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "reopening timer", ct);
        Assert.Equal(NotchView.Timer, coordinator.CurrentView);
        Assert.Equal(Visibility.Visible, window.TimerContent.Visibility);
    });

    [Fact]
    public void ExpansionKeepsContentCenteredThroughLayout() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        window.SetDebugViewState("MediaExpanded");
        int samples = 0;
        double maxError = 0;
        void Sample(object? _, EventArgs args)
        {
            if (!IsAnimating(window) || !window.ExpandedContent.IsVisible) return;
            var origin = window.ExpandedContent.TransformToAncestor(window.NotchContainer).Transform(new Point());
            double expected = (window.NotchContainer.ActualWidth - window.ExpandedContent.ActualWidth) / 2;
            maxError = Math.Max(maxError, Math.Abs(origin.X - expected));
            samples++;
        }
        CompositionTarget.Rendering += Sample;
        try { await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "centered expansion", ct); }
        finally { CompositionTarget.Rendering -= Sample; }
        Assert.True(samples > 0);
        Assert.InRange(maxError, 0, 1.5);
        Assert.Equal(HorizontalAlignment.Center, window.ExpandedContent.HorizontalAlignment);
    });

    [Fact]
    public void ClosingDuringMediaEntrancePreservesRenderedOpacityAndPosition() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await StartAsync(window, ct);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "initial expansion", ct);

        // Hold an in-flight entrance independently of the desktop's frame rate.
        window.ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);
        window.ExpandedContent.Opacity = 0;
        var clock = (System.Windows.Media.Animation.AnimationClock)new System.Windows.Media.Animation.DoubleAnimation(
            0, 1, TimeSpan.FromSeconds(1)).CreateClock(true);
        window.ExpandedContent.ApplyAnimationClock(UIElement.OpacityProperty, clock);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(350), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
        window.ExpandedContent.RenderTransform = new TranslateTransform(0, 17);
        double opacity = window.ExpandedContent.Opacity;
        double y = window.ExpandedContent.RenderTransform.Value.OffsetY;

        window.SetDebugViewState("CollapsedNotch");
        ExecutePendingTransition(window);

        Assert.Equal(opacity, window.ExpandedContent.Opacity, 6);
        Assert.Equal(y, window.ExpandedContent.RenderTransform.Value.OffsetY, 6);
        Assert.Equal(Visibility.Visible, window.ExpandedContent.Visibility);
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "continuous collapse", ct);
        Assert.Equal(Visibility.Collapsed, window.ExpandedContent.Visibility);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));

    private static void ExecutePendingTransition(MainWindow window)
    {
        // Dispatch synchronously at this sampled frame. Letting the test yield
        // would advance the clocks before checking continuity at the boundary.
        var pending = Field<TransitionRequestEventArgs>(window, "_pendingTransitionRequest");
        Assert.NotNull(pending);
        typeof(MainWindow).GetField("_pendingTransitionRequest", Private)!.SetValue(window, null);
        typeof(MainWindow).GetMethod("ExecuteTransitionRequest", Private)!.Invoke(window, [pending]);
    }

    private static async Task StartAsync(MainWindow window, CancellationToken ct)
    {
        window.SetDebugViewLock(true);
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !IsAnimating(window), "startup", ct);
        await Field<IMediaDetectionService>(window, "_mediaService").StopAsync();
        Field<UiScheduledTimer>(window, "_updateTimer").Stop();
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static T Property<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, Private)!.GetValue(window)!;
    private static bool IsAnimating(MainWindow window) => Property<bool>(window, "_isAnimating");
}
