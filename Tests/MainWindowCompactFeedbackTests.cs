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
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var battery = new BatteryInfo { Percentage = percent, IsCharging = pluggedIn, IsPluggedIn = pluggedIn, PowerWatts = watts, HasPowerRate = watts != 0 };
        object kind = Enum.Parse(typeof(MainWindow).GetNestedType("ChargingGlanceKind", BindingFlags.NonPublic)!, pluggedIn ? "PluggedIn" : "Unplugged");
        Invoke(window, "ShowChargingGlance", battery, kind);
        Assert.True(Field<bool>(window, "_isChargingNotificationVisible"));
        Assert.Equal($"{percent}%", window.ChargingPercentText.Text);
        Assert.Equal(Loc.Get(statusKey), window.ChargingStatusText.Text);
        Assert.Equal(formattedWatts, window.ChargingWattText.Text);
        Assert.Equal(watts == 0 ? Visibility.Collapsed : Visibility.Visible, window.ChargingWattText.Visibility);
        Assert.Equal(Math.Max(2, percent / 100d * 17), window.ChargingBatteryFill.Width);
        Assert.Equal(Visibility.Collapsed, window.CollapsedContent.Visibility);
        Assert.Equal(pluggedIn ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x95, 0), ((SolidColorBrush)window.ChargingPercentText.Foreground).Color);
        await WpfFrameWaiter.UntilAsync(() => window.ChargingNotification.Opacity == 1 && window.ChargingIconScale.ScaleX == 1, "charging glance entrance", ct);
        var timer = Field<DispatcherTimer>(window, "_chargingNotificationDismissTimer");
        timer.Interval = TimeSpan.FromMilliseconds(1);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isChargingNotificationVisible"), "charging glance automatic dismissal", ct);
        Assert.False(timer.IsEnabled);
        Assert.Null(Field<DispatcherTimer?>(window, "_chargingNotificationDismissTimer"));
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
            using var fixture = CreateFixture();
            var window = fixture.Window;
            Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 60, IsPluggedIn = false });
            Invoke(window, "HandleBatteryUpdate", new BatteryInfo { Percentage = 60, IsCharging = true, IsPluggedIn = true });
            Assert.True(Field<bool>(window, "_chargingPulseWanted"));
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
            Assert.Null(Field<DispatcherTimer?>(window, "_chargingNotificationDismissTimer"));
            Invoke(window, "CancelChargingGlanceImmediate");
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyFeedbackRestoresArtworkAfterTimerOrImmediateCancellation(bool cancel) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Set(window, "_isMusicCompactMode", true);
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = "Fixture song", MediaSource = "Spotify", IsPlaying = true });
        Invoke(window, "PlayClipboardPeek");
        Assert.True(Field<bool>(window, "_isClipboardPeekActive"));
        Assert.Equal(Loc.Get("clipboard.copied"), window.ClipboardCopiedText.Text);
        Assert.Equal(Visibility.Visible, window.MusicViz.Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.CompactThumbnailBorder.Visibility == Visibility.Collapsed && window.ClipboardCopiedText.Opacity == 1, "copy feedback entrance", ct);
        var timer = Field<DispatcherTimer>(window, "_clipboardRevertTimer");
        if (cancel)
        {
            Invoke(window, "CancelClipboardPeekImmediate");
            Assert.False(timer.IsEnabled);
            Assert.Equal(Visibility.Collapsed, window.ClipboardCopiedText.Visibility);
            Assert.Null(Field<DispatcherTimer?>(window, "_clipboardRevertTimer"));
        }
        else
        {
            timer.Interval = TimeSpan.FromMilliseconds(1);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isClipboardPeekActive") && window.ClipboardCopiedText.Visibility == Visibility.Collapsed && window.CompactThumbnailBorder.Opacity == 1, "copy feedback automatic revert", ct);
            Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
            Assert.Equal(1, window.CompactThumbnailScale.ScaleX);
            Assert.Null(Field<DispatcherTimer?>(window, "_clipboardRevertTimer"));
        }
        Invoke(window, "CancelClipboardPeekImmediate");
    });

    [Fact]
    public void CopyFeedbackOutsideMusicModeOnlyBouncesTheCollapsedNotch() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        Invoke(fixture.Window, "PlayClipboardPeek");
        Assert.False(Field<bool>(fixture.Window, "_isClipboardPeekActive"));
        await WpfFrameWaiter.UntilAsync(() => fixture.Window.NotchScale.ScaleX > 1, "copy bounce peak", ct);
        await WpfFrameWaiter.UntilAsync(() => fixture.Window.NotchScale.ScaleX == 1 && fixture.Window.NotchScale.ScaleY == 1, "copy bounce returns to rest", ct);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture()
    {
        var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        fixture.Window.NotchBorder.Width = 300;
        fixture.Window.NotchBorder.Height = 40;
        Set(fixture.Window, "_isNotchVisible", true);
        return fixture;
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value)
    {
        if (typeof(MainWindow).GetField(name, Private) is { } field) field.SetValue(window, value);
        else typeof(MainWindow).GetProperty(name, Private)!.SetValue(window, value);
    }
    private static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
