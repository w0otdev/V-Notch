using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowModulePresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivacyDotTracksActivitySuppressesDuringNotificationsAndRestoresItsPosition(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnablePrivacyIndicators = true;
        settings.EnableDynamicIslandMode = island;
        foreach (var state in new[] { PrivacyIndicatorState.Empty with { MicrophoneInUse = true }, PrivacyIndicatorState.Empty with { CameraInUse = true },
            PrivacyIndicatorState.Empty with { ScreenRecordingActive = true }, PrivacyIndicatorState.Empty with { LocationInUse = true } })
        {
            Invoke(window, "UpdatePrivacyIndicators", state);
            Assert.Equal(Visibility.Visible, window.PrivacyIndicatorPanel.Visibility);
            var expected = MainWindow.GetPrivacyDotColor(state);
            await WpfFrameWaiter.UntilAsync(() => window.PrivacyDotBrush.Color.R == expected.R && window.PrivacyDotBrush.Color.G == expected.G && window.PrivacyDotBrush.Color.B == expected.B, "privacy dot color", ct);
            Assert.True(window.PrivacyDot.HasAnimatedProperties);
        }
        Set(window, "_isMusicCompactMode", true);
        Invoke(window, "UpdatePrivacyDotPosition", true);
        await WpfFrameWaiter.UntilAsync(() => window.PrivacyIndicatorPanel.Margin.Right == 34, "privacy dot follows compact music", ct);
        Set(window, "_isVolumeIndicatorActive", true);
        Invoke(window, "SyncPrivacyDotVisibilityForCurrentView");
        Assert.Equal(Visibility.Collapsed, window.PrivacyIndicatorPanel.Visibility);
        Assert.False(window.PrivacyDot.HasAnimatedProperties);
        Set(window, "_isVolumeIndicatorActive", false);
        Invoke(window, "SyncPrivacyDotVisibilityForCurrentView");
        Assert.Equal(Visibility.Visible, window.PrivacyIndicatorPanel.Visibility);
        Invoke(window, "UpdatePrivacyIndicators", PrivacyIndicatorState.Empty);
        await WpfFrameWaiter.UntilAsync(() => window.PrivacyIndicatorPanel.Visibility == Visibility.Collapsed, "privacy activity ended", ct);
        Assert.Equal(Visibility.Collapsed, window.PrivacyDot.Visibility);
        settings.EnablePrivacyIndicators = false;
        Invoke(window, "PrivacyModule_StateChanged", null, PrivacyIndicatorState.Empty with { MicrophoneInUse = true });
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False(Field<bool>(window, "_privacyIndicatorsVisible"));
    });

    [Fact]
    public void OwnCameraPreviewDoesNotProduceAFalsePrivacyCameraNotification() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Field<NotchSettings>(window, "_settings").EnablePrivacyIndicators = true;
        var camera = Field<WebcamCaptureController>(window, "_camera");
        typeof(WebcamCaptureController).GetField("_isActive", Private)!.SetValue(camera, true);
        Invoke(window, "UpdatePrivacyIndicators", PrivacyIndicatorState.Empty with { CameraInUse = true, CameraConsumers = new[] { "V-Notch" } });
        Assert.False(Field<PrivacyIndicatorState>(window, "_lastPrivacyState").CameraInUse);
        Assert.Empty(Field<PrivacyIndicatorState>(window, "_lastPrivacyState").CameraConsumers);
        Assert.False(Field<bool>(window, "_privacyIndicatorsVisible"));
        Invoke(window, "UpdatePrivacyIndicators", PrivacyIndicatorState.Empty with { CameraInUse = true, MicrophoneInUse = true });
        Assert.Equal(MainWindow.GetPrivacyDotColor(PrivacyIndicatorState.Empty with { MicrophoneInUse = true }), window.PrivacyDotBrush.Color);
        Invoke(window, "StopCameraPreviewForViewExit", false);
    });

    [Theory]
    [InlineData(true, BluetoothDeviceType.Headphones)]
    [InlineData(false, BluetoothDeviceType.Headphones)]
    [InlineData(true, BluetoothDeviceType.Speaker)]
    [InlineData(false, BluetoothDeviceType.Keyboard)]
    [InlineData(true, BluetoothDeviceType.Mouse)]
    [InlineData(false, BluetoothDeviceType.GameController)]
    [InlineData(true, BluetoothDeviceType.Phone)]
    [InlineData(false, BluetoothDeviceType.Unknown)]
    public void BluetoothEventsShowDeviceStatusAndDismissOrCancelWithoutLeavingAStalePill(bool connected, BluetoothDeviceType type) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var device = new BluetoothDeviceInfo { Id = "fixture-device", Name = "Fixture audio device", DeviceType = type };
        Invoke(window, connected ? "BluetoothModule_DeviceConnected" : "BluetoothModule_DeviceDisconnected", null, device);
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isBluetoothNotificationVisible"), "bluetooth notification entered", ct);
        var grid = connected ? window.BluetoothNotification : window.BluetoothDisconnectNotification;
        Assert.Equal(Visibility.Visible, grid.Visibility);
        Assert.Equal(device.Name, connected ? window.BluetoothDeviceName.Text : window.BluetoothDisconnectDeviceName.Text);
        Assert.Equal(Loc.Get(connected ? "bluetooth.connected" : "bluetooth.disconnected"), connected ? window.BluetoothStatusText.Text : window.BluetoothDisconnectStatusText.Text);
        Assert.NotNull(connected ? window.BluetoothIcon.Data : window.BluetoothDisconnectIcon.Data);
        Invoke(window, "BluetoothController_DismissRequested");
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isBluetoothNotificationVisible"), "bluetooth notification dismissed", ct);
        Assert.Equal(Visibility.Collapsed, grid.Visibility);
        Assert.Equal(0, Field<int>(window, "_bluetoothNotificationToken"));
        Invoke(window, "BluetoothController_ShowRequested", device, !connected);
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isBluetoothNotificationVisible"), "replacement bluetooth notification", ct);
        Invoke(window, "CancelBluetoothNotificationImmediate");
        Assert.Equal(Visibility.Collapsed, window.BluetoothNotification.Visibility);
        Assert.Equal(Visibility.Collapsed, window.BluetoothDisconnectNotification.Visibility);
        Assert.False(Field<bool>(window, "_isBluetoothNotificationVisible"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeatherRevealStopsSkeletonAndShelfMirrorsDataThenHandlesUnavailableState(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            using var fixture = CreateFixture();
            var window = fixture.Window;
            var settings = Field<NotchSettings>(window, "_settings");
            settings.EnableWeather = true;
            settings.ShelfWidget = "weather";
            settings.ManualCity = "Bangkok";
            window.ApplyShelfWidgetMode();
            Invoke(window, "UpdateWeatherSkeletonState");
            Assert.True(window.WeatherWidgetSkeleton.HasAnimatedProperties);
            Assert.True(window.ShelfWeatherSkeleton.HasAnimatedProperties);
            var weather = new WeatherInfo { City = "Bangkok", Temperature = 31, High = 35, Low = 26, WeatherCode = 3, IsDay = true };
            Invoke(window, "WeatherModule_WeatherUpdated", null, new WeatherUpdateEventArgs { Weather = weather });
            await WpfFrameWaiter.UntilAsync(() => window.WeatherActualContent.Opacity == 1 && window.WeatherWidgetTranslate.Y == 0, "weather content revealed", ct);
            Assert.Equal("Bangkok", window.WeatherLocationText.Text);
            Assert.Equal("31°", window.WeatherTempText.Text);
            Assert.Equal(WeatherConditionFormatter.Format(3), window.WeatherConditionText.Text);
            Assert.Equal(window.WeatherTempText.Text, window.ShelfWeatherTempText.Text);
            Assert.Equal(window.WeatherConditionText.Text, window.ShelfWeatherDescText.Text);
            Assert.Equal("Bangkok", window.ShelfWeatherCityText.Text);
            Assert.Equal(Visibility.Collapsed, window.WeatherWidgetSkeleton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ShelfWeatherSkeleton.Visibility);
            Assert.False(window.WeatherWidgetSkeleton.HasAnimatedProperties);
            Invoke(window, "UpdateWeatherUI", new WeatherInfo { Temperature = 30, High = 35, Low = 25, WeatherCode = 0 });
            Assert.Equal("—", window.WeatherLocationText.Text);
            Invoke(window, "WeatherModule_WeatherUpdated", null, new WeatherUpdateEventArgs());
            Assert.Equal(Loc.Get("weather.unavailable"), window.WeatherLocationText.Text);
            settings.EnableWeather = false;
            Invoke(window, "WeatherModule_WeatherUpdated", null, new WeatherUpdateEventArgs());
            Assert.Equal(Loc.Get("weather.disabled"), window.WeatherLocationText.Text);
            settings.ShelfWidget = "none";
            window.ApplyShelfWidgetMode();
            Assert.False(window.ShelfWeatherSkeleton.HasAnimatedProperties);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ShelfWidgetsSwitchWithoutOpeningCameraAndClockStylesAreExclusive() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        window.SysMonCpuValueText.Text = "25%";
        window.SysMonRamValueText.Text = "50%";
        window.SysMonNetDownText.Text = "2 MB/s";
        window.SysMonNetUpText.Text = "1 MB/s";
        foreach (string widget in new[] { "sysmon", "clock", "weather", "none", "camera" })
        {
            settings.ShelfWidget = widget;
            window.ApplyShelfWidgetMode();
            Assert.Equal(widget == "camera" ? Visibility.Visible : Visibility.Collapsed, window.CameraSection.Visibility);
            Assert.Equal(widget == "sysmon" ? Visibility.Visible : Visibility.Collapsed, window.ShelfSysMonSection.Visibility);
            Assert.Equal(widget == "weather" ? Visibility.Visible : Visibility.Collapsed, window.ShelfWeatherSection.Visibility);
            Assert.Equal(widget == "clock" ? Visibility.Visible : Visibility.Collapsed, window.ShelfClockSection.Visibility);
            if (widget == "sysmon")
            {
                Assert.Equal("25%", window.ShelfSysMonCpuText.Text);
                Assert.Equal("50%", window.ShelfSysMonRamText.Text);
                Assert.Equal("↓ 2 MB/s", window.ShelfSysMonNetDownText.Text);
                Assert.Equal("↑ 1 MB/s", window.ShelfSysMonNetUpText.Text);
            }
            if (widget == "clock") Assert.Matches(@"^\d{2}:\d{2}$", window.ShelfClockTimeText.Text);
        }
        foreach (string style in new[] { "digital", "wordclock", "analog", "unknown" })
        {
            settings.ClockPageStyle = style;
            window.ApplyClockPageStyle();
            Assert.Equal(style == "digital" ? Visibility.Visible : Visibility.Collapsed, window.ClockViewDigitalClock.Visibility);
            Assert.Equal(style == "wordclock" ? Visibility.Visible : Visibility.Collapsed, window.ClockViewWordClock.Visibility);
            Assert.Equal(style is "analog" or "unknown" ? Visibility.Visible : Visibility.Collapsed, window.ClockViewClock.Visibility);
        }
        Assert.False(Field<WebcamCaptureController>(window, "_camera").IsLifecycleActive);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        configureSettings: settings => { settings.EnableLocalOnlyMode = true; },
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
