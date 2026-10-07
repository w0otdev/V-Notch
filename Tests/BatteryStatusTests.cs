using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class BatteryStatusTests
{
    [Theory]
    [InlineData(1, 1, 100, true, false)]
    [InlineData(1, 1, 80, true, false)]
    [InlineData(1, 2, 10, true, false)]
    [InlineData(0, 2, 10, false, true)]
    [InlineData(0, 9, 50, true, false)]
    [InlineData(1, 128, 100, false, false)]
    [InlineData(1, 255, 255, false, false)]
    public void BatteryPresentationShowsConnectedPowerEvenWhenTheChargingFlagIsClear(
        byte ac, byte flags, byte percentage, bool connected, bool low)
    {
        var info = new BatteryInfo();
        BatteryServiceImpl.ApplySystemPowerStatus(info, new()
        {
            ACLineStatus = ac,
            BatteryFlag = flags,
            BatteryLifePercent = percentage,
            BatteryLifeTime = -1
        });
        var viewModel = new SecondaryViewModel(new BatteryServiceImpl());
        viewModel.UpdateBattery(info);
        Assert.Equal(connected, info.IsPowerConnected);
        Assert.Equal(connected, info.GetBatteryIcon() == "⚡");
        Assert.Equal(connected, viewModel.IsBatteryCharging);
        Assert.Equal(low, viewModel.IsBatteryLow);
        Assert.Equal(info.HasBattery && (flags & 8) != 0, info.IsCharging);
    }

    [Theory]
    [InlineData(255, 255)]
    [InlineData(128, 100)]
    [InlineData(136, 50)]
    public void AbsentOrUnknownBatteryDoesNotBecomeAFullChargingBattery(byte flags, byte percent)
    {
        var info = new BatteryInfo();
        BatteryServiceImpl.ApplySystemPowerStatus(info, new()
        {
            ACLineStatus = 1,
            BatteryFlag = flags,
            BatteryLifePercent = percent,
            BatteryLifeTime = -1
        });
        Assert.False(info.HasBattery);
        Assert.False(info.IsCharging);
        Assert.True(info.IsPluggedIn);
        Assert.Equal(-1, info.Percentage);
        Assert.Equal("—", info.GetPercentageText());
        Assert.False(info.IsFullyCharged);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(0, 9, true)]
    [InlineData(1, 10, true)]
    [InlineData(255, 0, false)]
    public void ChargingUsesTheBatteryFlagIndependentlyOfAcPower(byte ac, byte flags, bool charging)
    {
        var info = new BatteryInfo();
        BatteryServiceImpl.ApplySystemPowerStatus(info, new()
        {
            ACLineStatus = ac,
            BatteryFlag = flags,
            BatteryLifePercent = 42,
            BatteryLifeTime = 3600
        });
        Assert.True(info.HasBattery);
        Assert.Equal(charging, info.IsCharging);
        Assert.Equal(ac == 1, info.IsPluggedIn);
        Assert.Equal(42, info.Percentage);
        Assert.Equal(60, info.RemainingMinutes);
    }

    [Fact]
    public void NtInformationRecoversUnknownPercentageAndCanConfirmBatteryAbsence()
    {
        var info = new BatteryInfo();
        BatteryServiceImpl.ApplySystemPowerStatus(info, new() { BatteryFlag = 255, BatteryLifePercent = 255 });
        BatteryServiceImpl.ApplyNtBatteryState(info, new()
        {
            BatteryPresent = true,
            AcOnLine = true,
            Charging = true,
            MaxCapacity = 50_000,
            RemainingCapacity = 25_000,
            Rate = 12_000
        });
        Assert.True(info.HasBattery);
        Assert.Equal(50, info.Percentage);
        Assert.True(info.IsCharging);
        Assert.Equal(12, info.PowerWatts);
        Assert.True(info.HasPowerRate);
        BatteryServiceImpl.ApplyNtBatteryState(info, new() { AcOnLine = true });
        Assert.False(info.HasBattery);
        Assert.Equal(-1, info.Percentage);
        Assert.False(info.IsCharging);
        Assert.False(info.HasPowerRate);
    }

    [Fact]
    public void KnownBatteryWithUnknownCapacityRemainsUnknown()
    {
        var info = new BatteryInfo();
        BatteryServiceImpl.ApplySystemPowerStatus(info, new() { BatteryFlag = 0, BatteryLifePercent = 255, BatteryLifeTime = -1 });
        BatteryServiceImpl.ApplyNtBatteryState(info, new() { BatteryPresent = true, MaxCapacity = uint.MaxValue, Rate = int.MinValue });
        Assert.True(info.HasBattery);
        Assert.Equal(-1, info.Percentage);
        Assert.False(info.HasPowerRate);
        Assert.Equal("—", info.GetPercentageText());
    }
}
