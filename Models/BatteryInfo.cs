namespace VNotch.Models;

public class BatteryInfo
{
    public int Percentage { get; set; } = 100;
    public bool IsCharging { get; set; } = false;
    public bool IsPluggedIn { get; set; } = false;
    public bool HasBattery { get; set; } = true;
    public int RemainingMinutes { get; set; } = -1;

    public double PowerWatts { get; set; } = 0.0;

    public bool HasPowerRate { get; set; } = false;

    public bool IsBatterySaver { get; set; } = false;

    public bool IsFullyCharged => HasBattery && IsPluggedIn && Percentage >= 99;

    public bool IsPowerConnected => HasBattery && (IsPluggedIn || IsCharging);

    public double FillRatio => HasBattery ? Math.Clamp(Percentage / 100.0, 0, 1) : 0;

    public string GetBatteryIcon()
    {
        if (IsPowerConnected) return "⚡";
        return Percentage >= 50 ? "🔋" : "🪫";
    }

    public string GetPercentageText()
    {
        return Percentage < 0 ? "—" : $"{Percentage}%";
    }
}
