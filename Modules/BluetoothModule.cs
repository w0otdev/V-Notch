using System;
using VNotch.Services;

namespace VNotch.Modules;

public class BluetoothModule : NotchModuleBase
{
    public override string ModuleName => "Bluetooth";
    public override TimeSpan? TickInterval => null;

    private readonly BluetoothMonitorService _bluetoothService;
    public event EventHandler<BluetoothDeviceInfo>? DeviceConnected;
    public event EventHandler<BluetoothDeviceInfo>? DeviceDisconnected;

    public BluetoothModule(BluetoothMonitorService bluetoothService)
    {
        _bluetoothService = bluetoothService;
    }

    protected override void OnStart()
    {
        _bluetoothService.DeviceConnected += OnDeviceConnected;
        _bluetoothService.DeviceDisconnected += OnDeviceDisconnected;
        _bluetoothService.Start();
    }

    protected override void OnStop()
    {
        _bluetoothService.DeviceConnected -= OnDeviceConnected;
        _bluetoothService.DeviceDisconnected -= OnDeviceDisconnected;
        _bluetoothService.Stop();
    }

    protected override void OnTick()
    {
    }

    private void OnDeviceConnected(object? sender, BluetoothDeviceInfo info)
    {
        DeviceConnected?.Invoke(this, info);
    }

    private void OnDeviceDisconnected(object? sender, BluetoothDeviceInfo info)
    {
        DeviceDisconnected?.Invoke(this, info);
    }
}
