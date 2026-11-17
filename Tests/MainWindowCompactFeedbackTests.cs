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
public sealed class MainWindowCompactFeedbackTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true, 50, 25, "battery.charging", "25 W")]
    [InlineData(true, 100, 5.5, "battery.fullyCharged", "5.5 W")]
    [InlineData(false, 20, -7.5, "battery.onBattery", "7.5 W")]
    [InlineData(false, 0, 0, "battery.onBattery", "")]
    public void PowerConnectionGlanceShowsChargeAndWattsThenRestoresTheCollapsedView(
        bool pluggedIn, int percent, double watts, string statusKey, string formattedWatts) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var battery = new BatteryInfo { Percentage = percent, IsCharging = pluggedIn, IsPluggedIn = pluggedIn, PowerWatts = watts, HasPowerRate = watts != 0 };
        object kind = Enum.Parse(typeof(MainWindow).GetNestedType("ChargingGlanceKind", BindingFlags.NonPublic)!, pluggedIn ? "PluggedIn" : "Unplugged");
        Invoke(window, "ShowChargingGlance", battery, kind);
        Assert.True(Field<bool>(window, "_isChargingNotificationVisible"));
        Assert.Equal($"{percent}%", window.ChargingPercentText.Text);
        Assert.Equal(Loc.Get(statusKey), window.ChargingStatusText.Text);
        Assert.Equal(formattedWatts, window.ChargingWattText.Text);
        Assert.Equal(watts == 0 ? Visibility.Collapsed : Visibility.Visible, window.ChargingWattText.Visibility);
        Assert.Equal(percent / 100d, window.ChargingBatteryFillScale.ScaleX);
        Assert.Equal(Visibility.Collapsed, window.CollapsedContent.Visibility);
        Assert.Equal(pluggedIn ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x95, 0), ((SolidColorBrush)window.ChargingPercentText.Foreground).Color);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.ChargingNotification.Opacity - 1) < 1e-6 && Math.Abs(window.ChargingIconScale.ScaleX - 1) < 1e-6, "charging glance entrance", ct);
        var timer = Field<UiScheduledTimer>(window, "_chargingNotificationDismissTimer");
        timer.Interval = TimeSpan.FromMilliseconds(1);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isChargingNotificationVisible"), "charging glance automatic dismissal", ct);
        Assert.False(timer.IsEnabled);
        Assert.Null(Field<UiScheduledTimer?>(window, "_chargingNotificationDismissTimer"));
        Assert.Equal(Visibility.Collapsed, window.ChargingNotification.Visibility);
        Assert.Equal(Visibility.Visible, window.CollapsedContent.Visibility);
        Invoke(window, "DismissChargingNotification");
    });

    [Fact]
    public void PowerConnectionEventsAndBatterySaverControlAmbientAnimations() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            await using var fixture = CreateFixture();
            var window = fixture.Window;
            await ShowWithoutLiveBatteryAsync(window, ct);
            Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 60, IsPluggedIn = false });
            Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 60, IsCharging = true, IsPluggedIn = true });
            Assert.True(Field<bool>(window, "_chargingPulseWanted"));
            // The pulsing fill is part of the expanded view, so it only animates while that view is shown.
            Assert.False(window.BatteryFill.IsVisible);
            Assert.Null(Field<object?>(window, "_chargingPulseStoryboard"));
            window.ExpandedContent.Visibility = Visibility.Visible;
            Assert.NotNull(Field<object?>(window, "_chargingPulseStoryboard"));
            window.ExpandedContent.Visibility = Visibility.Collapsed;
            Assert.Null(Field<object?>(window, "_chargingPulseStoryboard"));
            Assert.True(Field<bool>(window, "_chargingPulseWanted"));
            window.ExpandedContent.Visibility = Visibility.Visible;
            Assert.NotNull(Field<object?>(window, "_chargingPulseStoryboard"));
            Assert.Equal(Loc.Get("battery.charging"), window.ChargingStatusText.Text);
            Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 10, IsPluggedIn = false, IsBatterySaver = true });
            Assert.True(AnimationConfig.ReduceMotion);
            Assert.Null(Field<object?>(window, "_chargingPulseStoryboard"));
            Assert.Equal(Loc.Get("battery.onBattery"), window.ChargingStatusText.Text);
            await WpfFrameWaiter.UntilAsync(() => window.ChargingBolt.Opacity == 0 && Math.Abs(window.BatteryFillScale.ScaleX - 0.1) < 0.000001, "battery fill and charging bolt updated", ct);
            Assert.Equal(0.1, window.BatteryFillScale.ScaleX, 5);
            Invoke(window, "CancelChargingGlanceImmediate");
            Assert.False(Field<bool>(window, "_isChargingNotificationVisible"));
            Assert.Null(Field<UiScheduledTimer?>(window, "_chargingNotificationDismissTimer"));
            Invoke(window, "CancelChargingGlanceImmediate");
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void IdleTitleShimmerOnlyRunsWhileTheTitleIsVisible() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            await using var fixture = CreateFixture();
            var window = fixture.Window;
            await ShowWithoutLiveBatteryAsync(window, ct);
            window.ExpandedContent.Visibility = Visibility.Collapsed;
            Invoke(window, "StartTitleShimmer");
            Assert.True(Field<bool>(window, "_shimmerWanted"));
            Assert.False(Field<bool>(window, "_isShimmerActive"));
            window.ExpandedContent.Visibility = Visibility.Visible;
            Assert.True(window.MediaPlayback.TrackTitle.IsVisible);
            Assert.True(Field<bool>(window, "_isShimmerActive"));
            Assert.IsType<LinearGradientBrush>(window.MediaPlayback.TrackTitle.Foreground);
            window.ExpandedContent.Visibility = Visibility.Collapsed;
            Assert.False(Field<bool>(window, "_isShimmerActive"));
            Assert.True(Field<bool>(window, "_shimmerWanted"));
            Invoke(window, "StopTitleShimmer");
            window.ExpandedContent.Visibility = Visibility.Visible;
            Assert.False(Field<bool>(window, "_isShimmerActive"));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void SlowAmbientLoopsStayAtThirtyFpsOnAHighRefreshTarget() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousReduceMotion = AnimationConfig.ReduceMotion;
        const BindingFlags configFlags = BindingFlags.Static | BindingFlags.NonPublic;
        int previousFps = (int)typeof(AnimationConfig).GetField("_configuredFps", configFlags)!.GetValue(null)!;
        bool previousAutoFps = (bool)typeof(AnimationConfig).GetField("_autoFps", configFlags)!.GetValue(null)!;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            AnimationConfig.Configure(120, autoFps: false);
            Assert.Equal(120, AnimationConfig.TargetFps);
            await using var fixture = CreateFixture();
            var window = fixture.Window;
            await ShowWithoutLiveBatteryAsync(window, ct);
            window.ExpandedContent.Visibility = Visibility.Visible;
            Invoke(window, "StartTitleShimmer");
            Invoke(window, "StartChargingPulse");
            // One Forever clock above the display-rate floor makes WPF recompose the layered window at that rate.
            foreach (string name in new[] { "_titleShimmerStoryboard", "_chargingPulseStoryboard" })
            {
                var storyboard = Field<System.Windows.Media.Animation.Storyboard>(window, name);
                Assert.Equal(30, System.Windows.Media.Animation.Timeline.GetDesiredFrameRate(storyboard));
                Assert.All(storyboard.Children, child => Assert.Equal(30, System.Windows.Media.Animation.Timeline.GetDesiredFrameRate(child)));
            }
        }
        finally
        {
            AnimationConfig.Configure(previousFps, previousAutoFps);
            AnimationConfig.SetReduceMotion(previousReduceMotion);
        }
    });

    [Fact]
    public void CompactTitleMarqueeOnlyRunsWhileTheHoverInfoIsVisible() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            await using var fixture = CreateFixture();
            var window = fixture.Window;
            await ShowWithoutLiveBatteryAsync(window, ct);
            window.MusicCompactContent.Visibility = Visibility.Visible;
            Set(window, "_isMusicCompactMode", true);
            Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = new string('W', 120), CurrentArtist = "Fixture artist" });
            Set(window, "_isCompactThumbnailHovered", true);
            Invoke(window, "AnimateThumbnailHover", true);
            Assert.True(window.CompactHoverInfo.IsVisible);
            Assert.True(window.CompactTitleMarqueeTranslate.HasAnimatedProperties);
            // Collapsing the hover info after the pointer leaves must not leave the Forever clock running.
            window.CompactHoverInfo.Visibility = Visibility.Collapsed;
            Assert.False(window.CompactTitleMarqueeTranslate.HasAnimatedProperties);
            window.CompactHoverInfo.Visibility = Visibility.Visible;
            await WpfFrameWaiter.UntilAsync(() => window.CompactTitleMarqueeTranslate.HasAnimatedProperties, "marquee resumes when the hover info is shown again", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void CollapsingTheNotchStopsTheExpandedOnlyTimersAndRawMouseWorker() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await ShowWithoutLiveBatteryAsync(window, ct);
        var lyricsTimer = Field<UiScheduledTimer>(window, "_lyricsTimer");
        bool IsAnimating() => (bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!;
        ReflectionInvoker.InvokeWithDefaults(window, typeof(MainWindow).GetMethod("ExpandNotch", Private)!, new object?[] { null, NotchView.Media, NotchView.Compact });
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating() && lyricsTimer.IsEnabled && InputMonitorService.IsStarted, "expanded view starts its timers", ct);
        ReflectionInvoker.InvokeWithDefaults(window, typeof(MainWindow).GetMethod("CollapseNotch", Private)!, new object?[] { null });
        await WpfFrameWaiter.UntilAsync(() => !IsAnimating() && !lyricsTimer.IsEnabled && !InputMonitorService.IsStarted, "collapse stops expanded-only work", ct);
        Assert.False(Field<UiScheduledTimer>(window, "_volumeSyncTimer").IsEnabled);
    });

    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    [InlineData(10)]
    public void PluggedInBatteryKeepsItsBoltWhenActiveChargingStops(int percentage) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = percentage, IsPluggedIn = true });
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.ChargingBolt.Opacity - 1) < 1e-6, "plugged-in battery bolt", ct);
        Assert.False(Field<bool>(window, "_chargingPulseWanted"));
        var fillColor = ((SolidColorBrush)window.BatteryFill.Background).Color;
        Assert.Equal(((byte)0x30, (byte)0xD1, (byte)0x58), (fillColor.R, fillColor.G, fillColor.B));
        Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = percentage });
        await WpfFrameWaiter.UntilAsync(() => window.ChargingBolt.Opacity == 0, "unplugged battery bolt", ct);
    });

    [Fact]
    public void PluggingInAFullBatteryShowsFullyChargedGlanceWithoutAChargingFlag() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 100 });
        Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 100, IsPluggedIn = true });
        Assert.True(Field<bool>(window, "_isChargingNotificationVisible"));
        Assert.Equal(Loc.Get("battery.fullyCharged"), window.ChargingStatusText.Text);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyFeedbackRestoresArtworkAfterTimerOrImmediateCancellation(bool cancel) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        Set(window, "_isMusicCompactMode", true);
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = "Fixture song", MediaSource = "Spotify", IsPlaying = true });
        Invoke(window, "PlayClipboardPeek");
        Assert.True(Field<bool>(window, "_isClipboardPeekActive"));
        Assert.Equal(Loc.Get("clipboard.copied").TrimStart('✓', ' '), window.ClipboardCopiedText.Text);
        Assert.Equal(Visibility.Visible, window.MusicViz.Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.CompactThumbnailBorder.Visibility == Visibility.Collapsed && Math.Abs(window.ClipboardCopiedText.Opacity - 1) < 1e-6, "copy feedback entrance", ct);
        var timer = Field<UiScheduledTimer>(window, "_clipboardRevertTimer");
        if (cancel)
        {
            Invoke(window, "CancelClipboardPeekImmediate");
            Assert.False(timer.IsEnabled);
            Assert.Equal(Visibility.Collapsed, window.ClipboardCopiedText.Visibility);
            Assert.Null(Field<UiScheduledTimer?>(window, "_clipboardRevertTimer"));
        }
        else
        {
            timer.Interval = TimeSpan.FromMilliseconds(1);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isClipboardPeekActive") && window.ClipboardCopiedText.Visibility == Visibility.Collapsed && Math.Abs(window.CompactThumbnailBorder.Opacity - 1) < 1e-6, "copy feedback automatic revert", ct);
            Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
            Assert.Equal(1, window.CompactThumbnailScale.ScaleX);
            Assert.Null(Field<UiScheduledTimer?>(window, "_clipboardRevertTimer"));
        }
        Invoke(window, "CancelClipboardPeekImmediate");
    });

    [Fact]
    public void CopyFeedbackOutsideMusicModeOnlyBouncesTheCollapsedNotch() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        Invoke(fixture.Window, "PlayClipboardPeek");
        Assert.False(Field<bool>(fixture.Window, "_isClipboardPeekActive"));
        await WpfFrameWaiter.UntilAsync(() => fixture.Window.NotchScale.ScaleX > 1, "copy bounce peak", ct);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(fixture.Window.NotchScale.ScaleX - 1) < 1e-6 && Math.Abs(fixture.Window.NotchScale.ScaleY - 1) < 1e-6, "copy bounce returns to rest", ct);
    });

    [Fact]
    public void CollapsedClockShowsTheTimeAndOnlyTicksWhileItIsOnScreen() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        await ShowWithoutLiveBatteryAsync(window, ct);
        var timer = Field<DispatcherTimer>(window, "_collapsedClockTimer");
        static string Format(DateTime time) => time.ToString("t", System.Globalization.CultureInfo.CurrentCulture);

        window.CollapsedContent.Visibility = Visibility.Visible;
        var before = DateTime.Now;
        Invoke(window, "RefreshCollapsedClock");
        Assert.True(window.CollapsedClockText.IsVisible);
        Assert.Contains(window.CollapsedClockText.Text, new[] { Format(before), Format(DateTime.Now) });
        Assert.True(timer.IsEnabled);
        Assert.InRange(timer.Interval, TimeSpan.Zero, TimeSpan.FromSeconds(60.05));

        window.CollapsedContent.Visibility = Visibility.Collapsed;
        Assert.False(timer.IsEnabled);

        window.MusicCompactContent.Visibility = Visibility.Visible;
        Assert.True(window.CompactClockText.IsVisible);
        Assert.Equal(window.CollapsedClockText.Text, window.CompactClockText.Text);
        Assert.True(timer.IsEnabled);
        Assert.Equal(0, System.Windows.Controls.Grid.GetColumn(window.CompactThumbnailBorder));
        Assert.Equal(1, System.Windows.Controls.Grid.GetColumn(window.CompactClockText));
        Assert.Equal(2, System.Windows.Controls.Grid.GetColumn(window.MusicViz));

        window.ClipboardCopiedText.Visibility = Visibility.Visible;
        Assert.False(window.CompactClockText.IsVisible);
        window.ClipboardCopiedText.Visibility = Visibility.Collapsed;
        Assert.True(window.CompactClockText.IsVisible);

        Set(window, "_isNotchVisible", false);
        Invoke(window, "RefreshCollapsedClock");
        Assert.False(timer.IsEnabled);
    });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 500)]
    [InlineData(59, 999)]
    public void CollapsedClockTimerIsAlignedToTheNextMinute(int second, int millisecond)
    {
        var now = new DateTime(2026, 11, 17, 16, 20, second, millisecond);
        Assert.Equal(new DateTime(2026, 11, 17, 16, 21, 0).AddMilliseconds(50), now + MainWindow.GetDelayToNextMinute(now));
    }

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture()
    {
        var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        fixture.Window.NotchBorder.Width = 300;
        fixture.Window.NotchBorder.Height = 40;
        Set(fixture.Window, "_isNotchVisible", true);
        return fixture;
    }

    private static async Task ShowWithoutLiveBatteryAsync(MainWindow window, CancellationToken ct)
    {
        window.ShowActivated = false;
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_coreModulesStarted") && Field<bool>(window, "_isStartupLayoutReady") && !(bool)typeof(MainWindow).GetProperty("_isAnimating", Private)!.GetValue(window)!, "startup", ct);
        // The real battery would otherwise overwrite the fixture's battery updates.
        Field<VNotch.Modules.BatteryModule>(window, "_batteryModule").Stop();
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value)
    {
        if (typeof(MainWindow).GetField(name, Private) is { } field) field.SetValue(window, value);
        else typeof(MainWindow).GetProperty(name, Private)!.SetValue(window, value);
    }
    private static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
