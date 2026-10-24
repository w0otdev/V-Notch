using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public partial class BatteryServiceImpl : IBatteryService
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct SystemBatteryState
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool AcOnLine;
        [MarshalAs(UnmanagedType.U1)]
        public bool BatteryPresent;
        [MarshalAs(UnmanagedType.U1)]
        public bool Charging;
        [MarshalAs(UnmanagedType.U1)]
        public bool Discharging;
        public byte Spare1_0;
        public byte Spare1_1;
        public byte Spare1_2;
        public byte Spare1_3;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSystemBatteryState
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1_0;
        public byte Spare1_1;
        public byte Spare1_2;
        public byte Spare1_3;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnBatteryStateOut
    {
        public int Percentage;
        public byte IsCharging;
        public byte IsPluggedIn;
        public byte IsBatterySaver;
        public byte HasBattery;
        public int RemainingMinutes;
        public double PowerWatts;
        public byte HasPowerRate;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_battery_query_all(out VnBatteryStateOut outState);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_battery_apply_system_power_status(in VnSystemPowerStatus status, ref VnBatteryStateOut ioState);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_battery_apply_nt_battery_state(in VnSystemBatteryState state, ref VnBatteryStateOut ioState);

    public BatteryInfo GetBatteryInfo()
    {
        var info = new BatteryInfo { Percentage = -1, HasBattery = false };
        _ = vn_battery_query_all(out VnBatteryStateOut state);
        CopyToBatteryInfo(in state, info);
        return info;
    }

    internal static void ApplySystemPowerStatus(BatteryInfo info, SystemPowerStatus status)
    {
        var nativeStatus = new VnSystemPowerStatus
        {
            ACLineStatus = status.ACLineStatus,
            BatteryFlag = status.BatteryFlag,
            BatteryLifePercent = status.BatteryLifePercent,
            SystemStatusFlag = status.SystemStatusFlag,
            BatteryLifeTime = status.BatteryLifeTime,
            BatteryFullLifeTime = status.BatteryFullLifeTime,
        };
        var state = CopyFromBatteryInfo(info);
        _ = vn_battery_apply_system_power_status(in nativeStatus, ref state);
        CopyToBatteryInfo(in state, info);
    }

    internal static void ApplyNtBatteryState(BatteryInfo info, SystemBatteryState state)
    {
        // NT power information can recover a battery whose Win32 status was unknown.
        var nativeState = new VnSystemBatteryState
        {
            AcOnLine = state.AcOnLine ? (byte)1 : (byte)0,
            BatteryPresent = state.BatteryPresent ? (byte)1 : (byte)0,
            Charging = state.Charging ? (byte)1 : (byte)0,
            Discharging = state.Discharging ? (byte)1 : (byte)0,
            Spare1_0 = state.Spare1_0,
            Spare1_1 = state.Spare1_1,
            Spare1_2 = state.Spare1_2,
            Spare1_3 = state.Spare1_3,
            MaxCapacity = state.MaxCapacity,
            RemainingCapacity = state.RemainingCapacity,
            Rate = state.Rate,
            EstimatedTime = state.EstimatedTime,
            DefaultAlert1 = state.DefaultAlert1,
            DefaultAlert2 = state.DefaultAlert2,
        };
        var ioState = CopyFromBatteryInfo(info);
        _ = vn_battery_apply_nt_battery_state(in nativeState, ref ioState);
        CopyToBatteryInfo(in ioState, info);
    }

    private static VnBatteryStateOut CopyFromBatteryInfo(BatteryInfo info) =>
        new()
        {
            Percentage = info.Percentage,
            IsCharging = info.IsCharging ? (byte)1 : (byte)0,
            IsPluggedIn = info.IsPluggedIn ? (byte)1 : (byte)0,
            IsBatterySaver = info.IsBatterySaver ? (byte)1 : (byte)0,
            HasBattery = info.HasBattery ? (byte)1 : (byte)0,
            RemainingMinutes = info.RemainingMinutes,
            PowerWatts = info.PowerWatts,
            HasPowerRate = info.HasPowerRate ? (byte)1 : (byte)0,
        };

    private static void CopyToBatteryInfo(in VnBatteryStateOut state, BatteryInfo info)
    {
        info.Percentage = state.Percentage;
        info.IsCharging = state.IsCharging != 0;
        info.IsPluggedIn = state.IsPluggedIn != 0;
        info.IsBatterySaver = state.IsBatterySaver != 0;
        info.HasBattery = state.HasBattery != 0;
        info.RemainingMinutes = state.RemainingMinutes;
        info.PowerWatts = state.PowerWatts;
        info.HasPowerRate = state.HasPowerRate != 0;
    }
}
