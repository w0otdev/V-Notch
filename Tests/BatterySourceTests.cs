using VNotch.Models;
using VNotch.Services;
using VNotch.Modules;
using Xunit;

namespace VNotch.Tests;

public sealed class BatterySourceTests
{
    [Fact]
    public void SourceChangesDuringAReadPublishOnlyTheLatestChoice() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var service = new PendingBatteryService();
        using var module = new BatteryModule(service);
        var updated = new TaskCompletionSource<BatteryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.BatteryUpdated += (_, info) => updated.TrySetResult(info);
        module.SelectSource("first");
        module.Start();
        module.SelectSource("second");
        service.FirstRead.SetResult(new BatteryInfo { DeviceId = "first", Percentage = 10 });
        var info = await updated.Task.WaitAsync(ct);
        Assert.Equal("second", info.DeviceId);
        Assert.Equal(42, info.Percentage);
        Assert.Equal(new[] { "first", "second" }, service.Requests);
    });

    private sealed class PendingBatteryService : IBatteryService
    {
        internal TaskCompletionSource<BatteryInfo> FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Requests { get; } = new();
        public BatteryInfo GetBatteryInfo() => new();
        public Task<BatteryInfo> GetBatteryInfoAsync(string deviceId)
        {
            Requests.Add(deviceId);
            return deviceId == "first" ? FirstRead.Task : Task.FromResult(new BatteryInfo { DeviceId = deviceId, Percentage = 42 });
        }
    }

    private static BatteryInfo SystemBattery() => new()
    {
        Percentage = 87, HasBattery = true, IsCharging = true, IsPluggedIn = true,
        PowerWatts = 30, HasPowerRate = true, RemainingMinutes = 60, IsBatterySaver = true
    };

    [Fact]
    public void ExplicitBluetoothSourceDoesNotInheritComputerChargingOrCapacity()
    {
        var info = BatteryServiceImpl.SelectSource(SystemBattery(), "headset", [new("headset", "Headphones", 42, true)]);
        Assert.Equal("headset", info.DeviceId);
        Assert.Equal("Headphones", info.DeviceName);
        Assert.Equal(42, info.Percentage);
        Assert.True(info.HasBattery);
        Assert.False(info.IsPowerConnected);
        Assert.False(info.HasPowerRate);
        Assert.Equal(-1, info.RemainingMinutes);
        Assert.True(info.IsBatterySaver);
    }

    [Fact]
    public void AutoSelectSkipsDisconnectedAndUnknownDevicesAndFallsBackToSystem()
    {
        var system = SystemBattery();
        var devices = new BatteryDevice[] { new("offline", "Offline", 90, false), new("unknown", "Unknown", -1, true), new("mouse", "Mouse", 0, true) };
        Assert.Equal("mouse", BatteryServiceImpl.SelectSource(system, NotchSettings.AutoBluetoothBatteryDeviceId, devices).DeviceId);
        Assert.Same(system, BatteryServiceImpl.SelectSource(system, NotchSettings.AutoBluetoothBatteryDeviceId, devices[..2]));
        Assert.Same(system, BatteryServiceImpl.SelectSource(system, NotchSettings.SystemBatteryDeviceId, devices));
    }

    [Theory]
    [InlineData("headset", false, 51)]
    [InlineData("headset", true, -1)]
    [InlineData("headset", true, 101)]
    [InlineData("missing", true, 51)]
    public void UnavailableExplicitSourceNeverSilentlyBecomesSystemBattery(string selected, bool connected, int level)
    {
        var info = BatteryServiceImpl.SelectSource(SystemBattery(), selected, [new("headset", "Headphones", level, connected)]);
        Assert.Equal(selected, info.DeviceId);
        Assert.False(info.HasBattery);
        Assert.Equal("—", info.GetPercentageText());
        Assert.False(info.IsCharging);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(255, false)]
    public void WindowsBatteryPropertiesRejectUnknownAndInvalidPercentages(int level, bool valid)
    {
        int actual = BluetoothBatteryReader.ParsePercentage(new[] { (byte)level });
        Assert.Equal(valid ? level : -1, actual);
    }

    [Fact]
    public void MissingOrMalformedBatteryPropertyIsNotAReading()
    {
        Assert.Equal(-1, BluetoothBatteryReader.ParsePercentage(Array.Empty<byte>()));
        Assert.Equal(-1, BluetoothBatteryReader.ParsePercentage(new byte[] { 50, 0 }));
    }
}
