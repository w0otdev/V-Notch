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
    private string _deviceId = NotchSettings.SystemBatteryDeviceId;
    private int _sourceVersion;
    private bool _updating;

    public void SelectSource(string? deviceId)
    {
        deviceId ??= NotchSettings.SystemBatteryDeviceId;
        if (_deviceId == deviceId) return;
        _deviceId = deviceId;
        ++_sourceVersion;
        if (IsRunning) EmitUpdate();
    }

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

    private async void EmitUpdate()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            int version;
            BatteryInfo info;
            do
            {
                version = _sourceVersion;
                info = await _batteryService.GetBatteryInfoAsync(_deviceId);
            } while (version != _sourceVersion && IsRunning);
            if (IsRunning) BatteryUpdated?.Invoke(this, info);
        }
        catch (Exception ex) { RuntimeLog.Error("BATTERY", ex, "Could not update battery"); }
        finally { _updating = false; }
    }
}
