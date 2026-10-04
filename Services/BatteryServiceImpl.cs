using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public class BatteryServiceImpl : IBatteryService
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int InformationLevel,
        IntPtr InputBuffer,
        uint InputBufferLength,
        out SystemBatteryState OutputBuffer,
        uint OutputBufferLength);

    private const int SystemBatteryStateInfoLevel = 5;
    private const uint StatusSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemBatteryState
    {
        [MarshalAs(UnmanagedType.U1)] public bool AcOnLine;
        [MarshalAs(UnmanagedType.U1)] public bool BatteryPresent;
        [MarshalAs(UnmanagedType.U1)] public bool Charging;
        [MarshalAs(UnmanagedType.U1)] public bool Discharging;
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

    public BatteryInfo GetBatteryInfo()
    {
        var info = new BatteryInfo { Percentage = -1, HasBattery = false };
        PopulateSystemPowerStatus(info);
        PopulateNtBatteryState(info);
        return info;
    }

    private static void PopulateSystemPowerStatus(BatteryInfo info)
    {
        try
        {
            if (GetSystemPowerStatus(out SystemPowerStatus status))
            {
                ApplySystemPowerStatus(info, status);
            }
        }
        catch (Exception)
        {
            // Fallback defaults when power status cannot be queried
            info.Percentage = -1;
            info.HasBattery = false;
        }
    }

    internal static void ApplySystemPowerStatus(BatteryInfo info, SystemPowerStatus status)
    {
        info.HasBattery = status.BatteryFlag != byte.MaxValue && (status.BatteryFlag & 128) == 0;
        info.Percentage = info.HasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : -1;
        info.IsPluggedIn = status.ACLineStatus == 1;
        info.IsCharging = info.HasBattery && (status.BatteryFlag & 8) != 0;
        info.IsBatterySaver = (status.SystemStatusFlag & 1) != 0;
        info.RemainingMinutes = info.HasBattery && status.BatteryLifeTime >= 0 ? status.BatteryLifeTime / 60 : -1;
    }

    private static void PopulateNtBatteryState(BatteryInfo info)
    {
        try
        {
            uint size = (uint)Marshal.SizeOf<SystemBatteryState>();
            uint result = CallNtPowerInformation(SystemBatteryStateInfoLevel, IntPtr.Zero, 0, out SystemBatteryState bs, size);

            if (result == StatusSuccess)
            {
                ApplyNtBatteryState(info, bs);
            }
        }
        catch (Exception)
        {
            // NtPowerInformation query may not be supported or available
        }
    }

    internal static void ApplyNtBatteryState(BatteryInfo info, SystemBatteryState state)
    {
        info.HasBattery = state.BatteryPresent;
        info.IsPluggedIn = state.AcOnLine;
        info.IsCharging = state.BatteryPresent && state.Charging;
        if (!state.BatteryPresent)
        {
            info.Percentage = -1;
            info.RemainingMinutes = -1;
            info.PowerWatts = 0;
            info.HasPowerRate = false;
            return;
        }

        // NT power information can recover a battery whose Win32 status was unknown.
        if (info.Percentage < 0 && state.MaxCapacity > 0 && state.MaxCapacity != uint.MaxValue && state.RemainingCapacity != uint.MaxValue)
            info.Percentage = (int)Math.Min(100, state.RemainingCapacity * 100UL / state.MaxCapacity);

        info.HasPowerRate = Math.Abs((long)state.Rate) < 200_000;
        info.PowerWatts = info.HasPowerRate ? state.Rate / 1000.0 : 0;
    }
}
