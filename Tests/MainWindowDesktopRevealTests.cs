using System.Reflection;
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
        window.Opacity = target > 0.5 ? 0 : 1;
        int completed = 0;
        Invoke(window, "AnimateDesktopRevealOpacity", target, 80, new Action(() => completed++));
        await WpfFrameWaiter.UntilAsync(() => completed == 1, "desktop reveal opacity completion", ct);
        Assert.Equal(target, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
        Assert.False(window.HasAnimatedProperties);
        Invoke(window, "AnimateDesktopRevealOpacity", 0d, 120, new Action(() => completed += 100));
        Invoke(window, "AnimateDesktopRevealOpacity", 1d, 80, new Action(() => completed++));
        await WpfFrameWaiter.UntilAsync(() => completed == 2, "replacement desktop reveal completion", ct);
        Assert.Equal(1d, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
        Invoke(window, "AnimateDesktopRevealOpacity", 0d, 80, new Action(() => completed += 100));
        Invoke(window, "SetDesktopRevealOpacityImmediate", 0.7d);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.Equal(0.7, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
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
        Assert.Equal(1d, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
        Invoke(window, "PromoteFromDesktopLayer");
        Assert.True(Field<bool>(window, "_isDesktopEdgePromoted"));
        Assert.False(Field<bool>(window, "_desktopPromotionPending"));
        Assert.Equal(1d, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
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
        Assert.Equal(1d, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
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
        window.Opacity = 0;
        Invoke(window, "CancelPendingDesktopPromotion");
        Assert.False(Field<bool>(window, "_desktopPromotionPending"));
        Assert.Null(Field<EventHandler?>(window, "_desktopTransparentFrameHandler"));
        Assert.Equal(1d, window.ReadLocalValue(System.Windows.Window.OpacityProperty));
        Invoke(window, "CancelPendingDesktopPromotion");
        Invoke(window, "DetachDesktopTransparentFrameHandler");
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
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
