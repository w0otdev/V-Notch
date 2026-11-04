using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowDesktopRevealTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(1.0)]
    public void OpacityTransitionCommitsItsTargetAndOnlyLatestCompletionRuns(double target) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.DesktopRevealRoot.Opacity = target > 0.5 ? 0 : 1;
        var windowOpacity = window.ReadLocalValue(UIElement.OpacityProperty);
        int completed = 0;
        Invoke(window, "AnimateDesktopRevealOpacity", target, 80, new Action(() => completed++));
        await WpfFrameWaiter.UntilAsync(() => completed == 1, "desktop reveal opacity completion", ct);
        Assert.Equal(target, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
        Assert.False(window.DesktopRevealRoot.HasAnimatedProperties);
        Assert.Equal(windowOpacity, window.ReadLocalValue(UIElement.OpacityProperty));
        Invoke(window, "AnimateDesktopRevealOpacity", 0d, 120, new Action(() => completed += 100));
        Invoke(window, "AnimateDesktopRevealOpacity", 1d, 80, new Action(() => completed++));
        await WpfFrameWaiter.UntilAsync(() => completed == 2, "replacement desktop reveal completion", ct);
        Assert.Equal(1d, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
        Invoke(window, "AnimateDesktopRevealOpacity", 0d, 80, new Action(() => completed += 100));
        Invoke(window, "SetDesktopRevealOpacityImmediate", 0.7d);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(0.7, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Equal(2, completed);
    });

    [Fact]
    public void UnobscuredNotchChangesDesktopLayerWithoutLeavingItTransparent() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.StayBehindWindows = true;
        Invoke(window, "DemoteToDesktopLayerWithFade");
        Assert.False(Field<bool>(window, "_isDesktopEdgePromoted"));
        Assert.False(Field<bool>(window, "_desktopDemotionPending"));
        Assert.Equal(1d, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
        Invoke(window, "PromoteFromDesktopLayer");
        Assert.True(Field<bool>(window, "_isDesktopEdgePromoted"));
        Assert.False(Field<bool>(window, "_desktopPromotionPending"));
        Assert.Equal(1d, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
        Invoke(window, "PromoteFromDesktopLayer");
        Assert.True(Field<bool>(window, "_isDesktopEdgePromoted"));
        Assert.Null(Field<EventHandler?>(window, "_desktopTransparentFrameHandler"));
    });

    [Fact]
    public void StartupHoldPreventsScheduledDemotionAndDisablingDesktopModeClearsPendingWork() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        Field<NotchSettings>(window, "_settings").StayBehindWindows = true;
        window.StartStartupHold(TimeSpan.Zero);
        Assert.Null(Field<UiScheduledTimer?>(window, "_startupHoldTimer"));
        window.StartStartupHold(TimeSpan.FromSeconds(1));
        var first = Field<UiScheduledTimer>(window, "_startupHoldTimer");
        window.StartStartupHold(TimeSpan.FromSeconds(2));
        Assert.False(first.IsEnabled);
        Invoke(window, "ScheduleDesktopLayerDemotion");
        var demotion = Field<UiScheduledTimer>(window, "_desktopDemotionDelayTimer");
        Invoke(window, "ScheduleDesktopLayerDemotion");
        Assert.Same(demotion, Field<UiScheduledTimer>(window, "_desktopDemotionDelayTimer"));
        await WpfFrameWaiter.UntilAsync(() => !demotion.IsEnabled, "desktop demotion checks active hold", ct);
        Assert.True(Field<bool>(window, "_isDesktopEdgePromoted"));
        Set(window, "_desktopPromotionPending", true);
        Set(window, "_desktopDemotionPending", true);
        Set(window, "_desktopPointerInHoverZone", true);
        Field<NotchSettings>(window, "_settings").StayBehindWindows = false;
        Invoke(window, "ResetDesktopEdgePromotionIfDisabled");
        Assert.False(Field<bool>(window, "_desktopPromotionPending"));
        Assert.False(Field<bool>(window, "_desktopDemotionPending"));
        Assert.False(Field<bool>(window, "_desktopPointerInHoverZone"));
        Assert.Null(Field<UiScheduledTimer?>(window, "_startupHoldTimer"));
        Assert.Equal(1d, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
    });

    [Fact]
    public void CancelledPromotionDetachesItsRenderingHandlerAndRestoresOpacity() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        EventHandler handler = (_, _) => throw new InvalidOperationException("Cancelled handler should be detached before rendering.");
        Set(window, "_desktopPromotionPending", true);
        Set(window, "_desktopTransparentFrameHandler", handler);
        CompositionTarget.Rendering += handler;
        window.DesktopRevealRoot.Opacity = 0;
        Invoke(window, "CancelPendingDesktopPromotion");
        Assert.False(Field<bool>(window, "_desktopPromotionPending"));
        Assert.Null(Field<EventHandler?>(window, "_desktopTransparentFrameHandler"));
        Assert.Equal(1d, window.DesktopRevealRoot.ReadLocalValue(UIElement.OpacityProperty));
        Invoke(window, "CancelPendingDesktopPromotion");
        Invoke(window, "DetachDesktopTransparentFrameHandler");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlideAndFadeReverseWithoutResettingTheVisibleFrameOrIdleAnimation(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        bool originalReducedMotion = AnimationConfig.ReduceMotion;
        var windowOpacity = window.ReadLocalValue(UIElement.OpacityProperty);
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            window.NotchContainerTranslate.Y = -7;
            window.NotchContainer.Opacity = 0.6;
            int cancelled = 0, completed = 0;
            Invoke(window, "AnimateDesktopReveal", false, new Action(() => cancelled++));
            await WpfFrameWaiter.UntilAsync(() => window.DesktopRevealTranslate.Y < -1
                && window.DesktopRevealRoot.Opacity < 0.99, "desktop slides up and fades like idle", ct);
            double visibleY = window.DesktopRevealTranslate.Y;
            double visibleOpacity = window.DesktopRevealRoot.Opacity;
            int previousCompletions = cancelled;
            Invoke(window, "AnimateDesktopReveal", true, new Action(() => completed++));
            if (!reducedMotion && SystemParameters.ClientAreaAnimation)
            {
                Assert.Equal(visibleY, window.DesktopRevealTranslate.Y, 5);
                Assert.Equal(visibleOpacity, window.DesktopRevealRoot.Opacity, 5);
            }
            await WpfFrameWaiter.UntilAsync(() => completed == 1, "desktop reveal returns to its resting position", ct);
            Assert.Equal(previousCompletions, cancelled);
            Assert.Equal(0, window.DesktopRevealTranslate.Y);
            Assert.Equal(1, window.DesktopRevealRoot.Opacity);
            Assert.False(window.DesktopRevealTranslate.HasAnimatedProperties);
            Assert.False(window.DesktopRevealRoot.HasAnimatedProperties);
            Assert.Equal(-7, window.NotchContainerTranslate.Y);
            Assert.Equal(0.6, window.NotchContainer.Opacity);
            Assert.Equal(windowOpacity, window.ReadLocalValue(UIElement.OpacityProperty));
        }
        finally { AnimationConfig.SetReduceMotion(originalReducedMotion); }
    });

    [Fact]
    public void TransparentFrameBarrierWaitsForDistinctFramesAndIgnoresCancelledWork() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        int completed = 0;
        Invoke(window, "SetDesktopRevealOpacityImmediate", 0d);
        Invoke(window, "QueueAfterDesktopTransparentFrame", new Action(() => completed++));
        var handler = Field<EventHandler>(window, "_desktopTransparentFrameHandler");
        handler(window, Frame(10));
        handler(window, Frame(10));
        Assert.Equal(0, completed);
        handler(window, Frame(20));
        Assert.Equal(1, completed);
        Assert.Null(Field<EventHandler?>(window, "_desktopTransparentFrameHandler"));

        Invoke(window, "QueueAfterDesktopTransparentFrame", new Action(() => completed += 100));
        handler = Field<EventHandler>(window, "_desktopTransparentFrameHandler");
        Invoke(window, "SetDesktopRevealOpacityImmediate", 1d);
        handler(window, Frame(30));
        handler(window, Frame(40));
        Assert.Equal(1, completed);
    });

    [Fact]
    public void LateDesktopRestoreCannotExposeTheSurfaceDuringANewerPromotion() => SharedStaTestRunner.RunAsync(async _ =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        Set(window, "_isDesktopEdgePromoted", false);
        var windowOpacity = window.ReadLocalValue(UIElement.OpacityProperty);
        Invoke(window, "SetDesktopRevealOpacityImmediate", 0d);
        int version = Field<int>(window, "_desktopRevealAnimationVersion");
        Set(window, "_desktopPromotionPending", true);
        await (Task)Invoke(window, "CompleteDesktopLayerChangeAsync", version, false)!;
        Assert.Equal(0, window.DesktopRevealRoot.Opacity);

        Set(window, "_desktopPromotionPending", false);
        Invoke(window, "SetDesktopRevealOpacityImmediate", 0d);
        await (Task)Invoke(window, "CompleteDesktopLayerChangeAsync", version, false)!;
        Assert.Equal(0, window.DesktopRevealRoot.Opacity);
        await (Task)Invoke(window, "CompleteDesktopLayerChangeAsync",
            Field<int>(window, "_desktopRevealAnimationVersion"), false)!;
        Assert.Equal(1, window.DesktopRevealRoot.Opacity);
        Assert.Equal(0, window.DesktopRevealTranslate.Y);
        Assert.Equal(windowOpacity, window.ReadLocalValue(UIElement.OpacityProperty));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForegroundChangesRecheckDesktopDemotionWithoutMouseMovement(bool stayBehind) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Field<NotchShellState>(window, "_shellState").Hwnd = hwnd;
        Field<NotchSettings>(window, "_settings").StayBehindWindows = stayBehind;
        Set(window, "_startupHoldUntilUtc", DateTime.MinValue);
        Set(window, "_isDesktopEdgePromoted", true);
        Set(window, "_desktopPointerInHoverZone", false);

        // The startup hold blocks demotion. Expiring it and changing
        // foreground must recheck even though no mouse event has arrived.
        Set(window, "_startupHoldUntilUtc", DateTime.UtcNow.AddMinutes(1));
        Invoke(window, "OnForegroundWindowChanged", hwnd);
        Assert.False(Field<UiScheduledTimer?>(window, "_desktopDemotionDelayTimer")?.IsEnabled == true);
        Set(window, "_startupHoldUntilUtc", DateTime.MinValue);
        Invoke(window, "OnForegroundWindowChanged", hwnd);

        if (stayBehind)
        {
            Assert.True(Field<UiScheduledTimer>(window, "_desktopDemotionDelayTimer").IsEnabled);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isDesktopEdgePromoted"), "idle notch returns behind apps after focus changes", ct);
        }
        else Assert.False(Field<UiScheduledTimer?>(window, "_desktopDemotionDelayTimer")?.IsEnabled == true);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        configureServices: services => services.AddSingleton<VNotch.Services.IMediaDetectionService>(new FakeMediaDetectionService()));
    private static RenderingEventArgs Frame(int milliseconds) =>
        (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [TimeSpan.FromMilliseconds(milliseconds)], null)!;
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
