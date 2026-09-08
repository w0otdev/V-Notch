using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public class BatteryServiceImpl : IBatteryService
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
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
    private struct SystemBatteryState
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
        var info = new BatteryInfo();
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
                info.Percentage = status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent;
                info.IsCharging = status.ACLineStatus == 1;
                info.IsPluggedIn = status.ACLineStatus == 1;
                info.HasBattery = status.BatteryFlag != 128;

                info.IsBatterySaver = (status.SystemStatusFlag & 0x01) != 0;

                if (status.BatteryFlag == 8)
                {
                    info.IsCharging = true;
                }

                if (status.BatteryLifeTime != -1)
                {
                    info.RemainingMinutes = status.BatteryLifeTime / 60;
                }
            }
        }
        catch (Exception)
        {
            // Fallback defaults when power status cannot be queried
            info.Percentage = 100;
            info.HasBattery = false;
        }
    }

    private static void PopulateNtBatteryState(BatteryInfo info)
    {
        try
        {
            uint size = (uint)Marshal.SizeOf<SystemBatteryState>();
            uint result = CallNtPowerInformation(SystemBatteryStateInfoLevel, IntPtr.Zero, 0, out SystemBatteryState bs, size);

            if (result == StatusSuccess && bs.BatteryPresent)
            {
                int rateMilliwatts = bs.Rate;

                bool plausible = Math.Abs(rateMilliwatts) < 200_000;

                if (plausible)
                {
                    info.PowerWatts = rateMilliwatts / 1000.0;
                    info.HasPowerRate = true;
                }

                if (bs.Charging) info.IsCharging = true;
                if (bs.AcOnLine) info.IsPluggedIn = true;
            }
        }
        catch (Exception)
        {
            // NtPowerInformation query may not be supported or available
        }
    }
}
