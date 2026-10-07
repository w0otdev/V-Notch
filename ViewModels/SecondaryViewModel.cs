using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class SecondaryViewModel : ObservableObject
{
    private readonly IBatteryService _batteryService;
    [ObservableProperty]
    private string _batteryPercentText = "N/A";

    [ObservableProperty]
    private double _batteryFillWidth = 26;

    [ObservableProperty]
    private bool _isBatteryCharging;

    [ObservableProperty]
    private bool _isBatteryLow;

    [ObservableProperty]
    private string _calendarMonth = "";

    [ObservableProperty]
    private string _calendarDay = "";

    public SecondaryViewModel(IBatteryService batteryService) => _batteryService = batteryService;

    public void UpdateBattery()
    {
        try { UpdateBattery(_batteryService.GetBatteryInfo()); }
        catch { BatteryPercentText = "N/A"; }
    }

    public void UpdateBattery(BatteryInfo battery)
    {
        BatteryPercentText = battery.GetPercentageText();
        BatteryFillWidth = Math.Max(2, battery.Percentage / 100.0 * 26);
        IsBatteryCharging = battery.IsPowerConnected;
        IsBatteryLow = battery.HasBattery && battery.Percentage >= 0 && battery.Percentage < 20 && !battery.IsPowerConnected;
    }

    public void UpdateCalendar(DateTime? value = null)
    {
        var now = value ?? DateTime.Now;
        CalendarMonth = now.ToString("MMM");
        CalendarDay = now.Day.ToString();
    }
}
