using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowBatterySourceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ScreenReaderAndBluetoothIconFollowTheDisplayedSource() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reducedMotion = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        try
        {
            var window = fixture.Window;
            var update = typeof(MainWindow).GetMethod("BatteryModule_BatteryUpdated", Private)!;
            update.Invoke(window, [null, new BatteryInfo { DeviceId = "headset", DeviceName = "Headphones", Percentage = 42 }]);
            Assert.Equal("Headphones, 42%", AutomationProperties.GetName(window.BatterySection));
            Assert.Equal(Visibility.Visible, window.LinkedDeviceBatteryIconView.Visibility);
            update.Invoke(window, [null, new BatteryInfo { DeviceId = "headset", DeviceName = "Headphones", Percentage = -1, HasBattery = false }]);
            Assert.Contains(Loc.Get("settings.batteryDevice.unavailable", "Headphones"), AutomationProperties.GetName(window.BatterySection));
            update.Invoke(window, [null, new BatteryInfo { Percentage = 87 }]);
            Assert.Equal($"{Loc.Get("settings.batteryDevice.system")}, 87%", AutomationProperties.GetName(window.BatterySection));
            Assert.Equal(Visibility.Collapsed, window.LinkedDeviceBatteryIconView.Visibility);
        }
        finally { AnimationConfig.SetReduceMotion(reducedMotion); }
    });

    [Fact]
    public void IntroAcknowledgementPersistsWithoutSavingLivePreview() => SharedStaTestRunner.RunAsync(async () =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings => settings.EnableLocalOnlyMode = true,
            services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var service = (ISettingsService)typeof(MainWindow).GetField("_settingsService", Private)!.GetValue(window)!;
        int committedWidth = service.Load().Width;
        ((NotchSettings)typeof(MainWindow).GetField("_settings", Private)!.GetValue(window)!).Width += 50;
        await window.MarkSpotlightIntroSeenAsync();
        Assert.True(service.Load().HasSeenSpotlightIntro);
        Assert.Equal(committedWidth, service.Load().Width);
    });
}
