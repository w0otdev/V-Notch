using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowScreenCaptureTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("HandleAppDeactivated")]
    [InlineData("MainWindow_Deactivated")]
    public void CaptureFocusChangesKeepTimerViewOpen(string handler) => WithOpenView(NotchView.Timer, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => true;
        Invoke(window, handler, handler == "MainWindow_Deactivated" ? [null, EventArgs.Empty] : []);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(NotchView.Timer, coordinator.TargetView);
        Assert.False(coordinator.IsTransitionActive);
    });

    [Theory]
    [InlineData(NotchView.Media)]
    [InlineData(NotchView.Secondary)]
    [InlineData(NotchView.Timer)]
    [InlineData(NotchView.AudioMixer)]
    public void SelectingAScreenshotRegionKeepsTheCurrentView(NotchView view) => WithOpenView(view, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => true;
        ClickOutside(window);
        // A capture overlay can disappear before the queued mouse callback drains.
        window.ScreenCaptureActivityProbe = () => false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(view, coordinator.TargetView);
        Assert.False(coordinator.IsTransitionActive);
    });

    [Fact]
    public void ScreenshotFullscreenNotificationCannotHideOrCollapseTheNotch() => WithOpenView(NotchView.Media, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => true;
        Invoke(window, "FullscreenController_HideStateChanged", true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(Field<NotchShellState>(window, "_shellState").IsHiddenByFullscreen);
        Assert.Equal(NotchView.Media, coordinator.TargetView);
    });

    [Fact]
    public void PendingHoverTimerCannotCloseTheViewDuringCapture() => WithOpenView(NotchView.Media, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => true;
        Keyboard.ClearFocus();
        SetField(window, "_suppressHoverCollapseUntilUtc", DateTime.MinValue);
        var timer = Field<UiScheduledTimer>(window, "_hoverCollapseTimer");
        timer.Interval = TimeSpan.FromMilliseconds(1);
        timer.Start();
        await WpfFrameWaiter.UntilAsync(() => !timer.IsEnabled, "pending capture hover timer", ct);
        Assert.Equal(NotchView.Media, coordinator.TargetView);
    });

    [Fact]
    public void OutsideClicksStillCloseTheViewWithoutCapture() => WithOpenView(NotchView.Secondary, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => false;
        ClickOutside(window);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(NotchView.Compact, coordinator.TargetView);
    });

    [Fact]
    public void CaptureBeginningBeforeQueuedOutsideInputDrainsKeepsTheView() => WithOpenView(NotchView.Media, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => false;
        ClickOutside(window);
        window.ScreenCaptureActivityProbe = () => true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(NotchView.Media, coordinator.TargetView);
    });

    [Fact]
    public void CaptureFocusLossDoesNotDemoteAnOpenDesktopNotch() => WithOpenView(NotchView.Media, async (window, coordinator, ct) =>
    {
        window.ScreenCaptureActivityProbe = () => true;
        Assert.True((bool)Invoke(window, "IsDesktopNotchInteractionActive")!);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(NotchView.Media, coordinator.TargetView);
    });

    [Fact]
    public void CapturePreventsAnEmptyCompactNotchFromBecomingIdle() => SharedStaTestRunner.RunAsync(async () =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        fixture.Window.ScreenCaptureActivityProbe = () => false;
        Assert.True((bool)Invoke(fixture.Window, "IsNotchEmptyAndIdle")!);
        fixture.Window.ScreenCaptureActivityProbe = () => true;
        Assert.False((bool)Invoke(fixture.Window, "IsNotchEmptyAndIdle")!);
    });

    private static void ClickOutside(MainWindow window)
    {
        var wrapper = (FrameworkElement)window.FindName("NotchWrapper");
        var point = wrapper.PointToScreen(new Point(wrapper.ActualWidth + 300, wrapper.ActualHeight + 300));
        Invoke(window, "GlobalMouseHook_MouseLeftButtonDown", null, new InputMonitorService.POINT { x = (int)point.X, y = (int)point.Y });
    }

    private static void WithOpenView(NotchView view, Func<MainWindow, NotchTransitionCoordinator, CancellationToken, Task> test)
        => SharedStaTestRunner.RunAsync(async ct =>
        {
            bool reducedMotion = AnimationConfig.ReduceMotion;
            await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
                settings =>
                {
                    settings.EnableLocalOnlyMode = true;
                    settings.DisableMouseLeaveAutoClose = true;
                    settings.HideOnExclusiveFullscreen = false;
                    settings.HideOnWindowedFullscreen = false;
                },
                services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
            var window = fixture.Window;
            try
            {
                AnimationConfig.SetReduceMotion(true);
                window.SetDebugViewLock(true);
                window.ShowActivated = false;
                window.Show();
                await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !IsAnimating(window), "startup", ct);
                Field<IMediaDetectionService>(window, "_mediaService").Stop();
                var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
                coordinator.RequestView(view, "ScreenCaptureRegression");
                await WpfFrameWaiter.UntilAsync(() => !IsAnimating(window), "expanded view", ct);
                window.SetDebugViewLock(false);
                SetField(window, "_suppressOutsideClickUntilUtc", DateTime.MinValue);
                Assert.Equal(view, coordinator.CurrentView);
                await test(window, coordinator, ct);
            }
            finally { AnimationConfig.SetReduceMotion(reducedMotion); }
        });

    private static object? Invoke(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void SetField(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static bool IsAnimating(MainWindow window) =>
        (bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!;
}
