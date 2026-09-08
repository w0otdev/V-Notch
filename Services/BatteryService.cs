using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public static class BatteryService
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

    public static BatteryInfo GetBatteryInfo()
    {
        var info = new BatteryInfo();

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

        return info;
    }
}
