using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class SecondaryViewModel : ObservableObject
{
    private readonly IBatteryService _batteryService;
    [ObservableProperty]
    private string _batteryPercentText = "N/A";

    public SecondaryViewModel(IBatteryService batteryService) => _batteryService = batteryService;

    public void UpdateBattery()
    {
        try { UpdateBattery(_batteryService.GetBatteryInfo()); }
        catch { BatteryPercentText = "N/A"; }
    }

    public void UpdateBattery(BatteryInfo battery)
    {
        BatteryPercentText = battery.GetPercentageText();
    }
}
