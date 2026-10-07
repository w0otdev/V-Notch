using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowClipboardDismissalTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropFeedbackSettlesAndClearsWhenTheTrayCloses(bool reducedMotion) => WithOpenTray(async (window, coordinator, ct) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            var glow = (FrameworkElement)window.FindName("ClipboardDropGlow");
            var badge = (FrameworkElement)window.FindName("ClipboardDropBadge");
            Invoke(window, "SetClipboardDropHover", true);
            await WpfFrameWaiter.UntilAsync(() => glow.Opacity > 0.6, "drag target highlighted", ct);
            Invoke(window, "PlayClipboardDropFeedback");
            Assert.Equal(1, badge.Opacity);
            Assert.False(badge.IsHitTestVisible);
            await WpfFrameWaiter.UntilAsync(() => badge.Opacity == 0 && glow.Opacity == 0, "drop confirmation settled", ct);
            Assert.Equal(NotchView.Secondary, coordinator.TargetView);
            Invoke(window, "PlayClipboardDropFeedback");
            coordinator.RequestCollapse("FeedbackTest");
            Assert.Equal(0, badge.Opacity);
            Assert.Equal(0, glow.Opacity);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void OleDragLeaveInsideTheNotchKeepsTheTrayOpen() => WithOpenTray(async (window, coordinator, ct) =>
    {
        SetField(window, "_clipboardDragAutoExpanded", true);
        SetField(window, "_clipboardDragCanDrop", true);
        Invoke(window, "NotchWrapper_DragLeave", window, null);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
        Assert.NotNull(Field<DispatcherTimer?>(window, "_clipboardDragTimer"));
        Invoke(window, "CompleteClipboardDragLeave", true);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
        Assert.True(Field<bool>(window, "_clipboardDragCanDrop"));
        Assert.Null(Field<DispatcherTimer?>(window, "_clipboardDragTimer"));
    });

    [Theory]
    [InlineData("NotchWrapper_PreviewDragOver", true)]
    [InlineData("NotchWrapper_PreviewDrop", false)]
    public void ChildDragTargetsCancelPendingCollapse(string handler, bool autoExpanded) => WithOpenTray(async (window, coordinator, ct) =>
    {
        SetField(window, "_clipboardDragAutoExpanded", true);
        Invoke(window, "NotchWrapper_DragLeave", window, null);
        var timer = Field<DispatcherTimer>(window, "_clipboardDragTimer");
        Invoke(window, handler, window, null);
        Assert.False(timer.IsEnabled);
        Assert.Null(Field<DispatcherTimer?>(window, "_clipboardDragTimer"));
        Assert.Equal(autoExpanded, Field<bool>(window, "_clipboardDragAutoExpanded"));
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
    });

    [Theory]
    [InlineData(true, NotchView.Compact)]
    [InlineData(false, NotchView.Secondary)]
    public void LeavingTheNotchOnlyClosesAnAutoOpenedTray(bool autoExpanded, NotchView expected) => WithOpenTray(async (window, coordinator, ct) =>
    {
        SetField(window, "_clipboardDragAutoExpanded", autoExpanded);
        SetField(window, "_clipboardDragCanDrop", true);
        Invoke(window, "CompleteClipboardDragLeave", false);
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "drag leave settled", ct);
        Assert.Equal(expected, coordinator.TargetView);
        Assert.False(Field<bool>(window, "_clipboardDragCanDrop"));
        Assert.False(Field<bool>(window, "_clipboardDragAutoExpanded"));
    });

    [Fact]
    public void NativeAndWpfDeactivationLeaveTheFileTrayOpen() => WithOpenTray(async (window, coordinator, ct) =>
    {
        Invoke(window, "HandleAppDeactivated");
        Invoke(window, "MainWindow_Deactivated", null, EventArgs.Empty);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(NotchView.Secondary, coordinator.CurrentView);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
        Assert.False(coordinator.IsTransitionActive);
        Assert.Equal(Visibility.Visible, window.SecondaryContent.Visibility);
    });

    [Fact]
    public void OneOutsideClickClosesTheTrayWhileInsideClicksKeepItOpen() => WithOpenTray(async (window, coordinator, ct) =>
    {
        var wrapper = (FrameworkElement)window.FindName("NotchWrapper");
        var inside = wrapper.PointToScreen(new Point(wrapper.ActualWidth / 2, wrapper.ActualHeight / 2));
        Invoke(window, "GlobalMouseHook_MouseLeftButtonDown", null, MousePoint(inside));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);

        var outside = wrapper.PointToScreen(new Point(wrapper.ActualWidth + 300, wrapper.ActualHeight + 300));
        Invoke(window, "GlobalMouseHook_MouseLeftButtonDown", null, MousePoint(outside));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(NotchView.Compact, coordinator.TargetView);
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "outside-click dismissal", ct);
        Assert.Equal(NotchView.Compact, coordinator.CurrentView);
    });

    [Fact]
    public void AHoverTimerLeftOverFromAnotherViewCannotCloseTheTray() => WithOpenTray(async (window, coordinator, ct) =>
    {
        Keyboard.ClearFocus();
        SetField(window, "_suppressHoverCollapseUntilUtc", DateTime.MinValue);
        var timer = Field<DispatcherTimer>(window, "_hoverCollapseTimer");
        timer.Interval = TimeSpan.FromMilliseconds(1);
        timer.Start();
        await WpfFrameWaiter.UntilAsync(() => !timer.IsEnabled, "pending hover timer", ct);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
        Assert.False(coordinator.IsTransitionActive);
    });

    [Fact]
    public void FileTrayKeepsItsDesktopPromotionAfterLosingKeyboardFocus() => WithOpenTray(async (window, coordinator, ct) =>
    {
        Keyboard.ClearFocus();
        SetField(window, "_startupHoldUntilUtc", DateTime.MinValue);
        SetField(window, "_desktopPointerInHoverZone", false);
        Assert.True((bool)Invoke(window, "IsDesktopNotchInteractionActive")!);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
    });

    [Fact]
    public void AnOpenTrayRestoresFullscreenHiddenStateWithoutChangingUserSettings() => WithOpenTray(async (window, coordinator, ct) =>
    {
        var fullscreen = Field<FullscreenAutoHideController>(window, "_fullscreenController");
        var settings = Field<NotchSettings>(window, "_settings");
        settings.HideOnExclusiveFullscreen = true;
        settings.HideOnWindowedFullscreen = true;
        typeof(FullscreenAutoHideController).GetField("_isHiddenByFullscreen", Private)!.SetValue(fullscreen, true);
        var changes = new List<bool>();
        fullscreen.HideStateChanged += changes.Add;

        Assert.True(fullscreen.Evaluate(force: true));
        Assert.False(fullscreen.IsHiddenByFullscreen);
        Assert.Equal(new[] { false }, changes);
        Assert.True(settings.HideOnExclusiveFullscreen);
        Assert.True(settings.HideOnWindowedFullscreen);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(NotchView.Secondary, coordinator.TargetView);
    });

    private static void WithOpenTray(Func<MainWindow, NotchTransitionCoordinator, CancellationToken, Task> test)
        => SharedStaTestRunner.RunAsync(async ct =>
        {
            using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
                settings =>
                {
                    settings.EnableLocalOnlyMode = true;
                    settings.DisableMouseLeaveAutoClose = true;
                    settings.HideOnExclusiveFullscreen = false;
                    settings.HideOnWindowedFullscreen = false;
                },
                services => services.AddSingleton<IMediaDetectionService>(new Fakes.FakeMediaDetectionService()));
            var window = fixture.Window;
            window.SetDebugViewLock(true);
            window.ShowActivated = false;
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !IsAnimating(window), "startup", ct);
            Field<IMediaDetectionService>(window, "_mediaService").Stop();
            window.SetDebugViewState("SecondaryShelf");
            await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "file tray expansion", ct);
            window.SetDebugViewLock(false);
            SetField(window, "_suppressOutsideClickUntilUtc", DateTime.MinValue);
            var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
            Assert.Equal(NotchView.Secondary, coordinator.CurrentView);
            await test(window, coordinator, ct);
        });

    private static InputMonitorService.POINT MousePoint(Point point) => new() { x = (int)point.X, y = (int)point.Y };
    private static object? Invoke(MainWindow window, string method, params object?[] args)
        => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void SetField(MainWindow window, string name, object value)
        => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static bool IsAnimating(MainWindow window)
        => (bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!;
}
