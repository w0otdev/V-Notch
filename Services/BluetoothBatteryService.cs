using VNotch.Models;

namespace VNotch.Services;

/// <summary>Reads Windows' battery properties without opening a device connection.</summary>
internal sealed class BluetoothBatteryService
{
    internal static BluetoothBatteryService Instance { get; } = new();
    private readonly object _gate = new();
    private Task<IReadOnlyList<BatteryDevice>>? _refresh;
    private IReadOnlyList<BatteryDevice> _devices = Array.Empty<BatteryDevice>();
    private DateTime _refreshedUtc;

    internal Task<IReadOnlyList<BatteryDevice>> GetDevicesAsync(bool forceRefresh = false)
    {
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false }) return _refresh;
            if (!forceRefresh && DateTime.UtcNow - _refreshedUtc < TimeSpan.FromSeconds(10))
                return Task.FromResult(_devices);
            return _refresh = Task.Run(Refresh);
        }
    }

    private IReadOnlyList<BatteryDevice> Refresh()
    {
        IReadOnlyList<BatteryDevice> devices;
        try { devices = BluetoothBatteryReader.ReadDevices(); }
        catch (Exception ex)
        {
            RuntimeLog.Warn("BLUETOOTH-BATTERY", $"Could not enumerate battery sources: {ex.Message}");
            lock (_gate) devices = _devices.Select(device => device with { IsConnected = false }).ToArray();
        }
        lock (_gate)
        {
            _devices = devices;
            _refreshedUtc = DateTime.UtcNow;
        }
        return devices;
    }
}
