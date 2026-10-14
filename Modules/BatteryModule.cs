using System;
using Microsoft.Win32;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public class BatteryModule : NotchModuleBase
{
    public override string ModuleName => "Battery";

    public override TimeSpan? TickInterval => TimeSpan.FromSeconds(15);

    private readonly IBatteryService _batteryService;

    public event EventHandler<BatteryInfo>? BatteryUpdated;

    public BatteryModule(IBatteryService batteryService)
    {
        _batteryService = batteryService;
    }

    protected override void OnStart()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    protected override void OnStop()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    protected override void OnDispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        base.OnDispose();
    }

    protected override void OnTick() => EmitUpdate();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(EmitUpdate));
        else
            EmitUpdate();
    }

    private void EmitUpdate()
    {
        var info = _batteryService.GetBatteryInfo();
        BatteryUpdated?.Invoke(this, info);
    }
}
