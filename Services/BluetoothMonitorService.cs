using System;
using System.Collections.Concurrent;
using System.Linq;
using Windows.Devices.Enumeration;

namespace VNotch.Services;

public sealed class BluetoothMonitorService : IDisposable
{
    private const string LogTag = "BLUETOOTH";
    private static readonly char[] IdSeparators = { '#', '\\' };

    private DeviceWatcher? _watcher;
    private readonly ConcurrentDictionary<string, BluetoothDeviceInfo> _knownDevices = new();
    private readonly Debouncer _debouncer;
    private bool _disposed;
    private bool _isInitialEnumerationComplete = false;
    public event EventHandler<BluetoothDeviceInfo>? DeviceConnected;
    public event EventHandler<BluetoothDeviceInfo>? DeviceDisconnected;

    public BluetoothMonitorService()
    {
        _debouncer = new Debouncer(TimeSpan.FromMilliseconds(300));
    }

    public void Start()
    {
        if (_watcher != null) return;

        try
        {
            string aqsFilter = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"" +
                               " AND System.Devices.Aep.IsConnected:=System.StructuredQueryType.Boolean#True";

            string[] requestedProperties = new[]
            {
                "System.Devices.Aep.DeviceAddress",
                "System.Devices.Aep.IsConnected",
                "System.Devices.Aep.Category"
            };

            _watcher = DeviceInformation.CreateWatcher(
                aqsFilter,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

            _watcher.Added += Watcher_Added;
            _watcher.Updated += Watcher_Updated;
            _watcher.Removed += Watcher_Removed;
            _watcher.EnumerationCompleted += Watcher_EnumerationCompleted;
            _watcher.Stopped += Watcher_Stopped;

            _watcher.Start();
            RuntimeLog.Log(LogTag, "DeviceWatcher started");
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "Failed to start DeviceWatcher");
        }
    }

    public void Stop()
    {
        if (_watcher == null) return;

        try
        {
            if (_watcher.Status == DeviceWatcherStatus.Started ||
                _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "Failed to stop DeviceWatcher");
        }
    }

    private void Watcher_Added(DeviceWatcher sender, DeviceInformation device)
    {
        var info = CreateDeviceInfo(device);
        if (info == null) return;

        _knownDevices[device.Id] = info;
        RuntimeLog.Log(LogTag, $"Device connected: {info.Name} ({info.DeviceType})");

        if (_isInitialEnumerationComplete)
        {
            _debouncer.Debounce(() => DeviceConnected?.Invoke(this, info));
        }
    }

    private void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connectedObj)
            && connectedObj is bool isConnected)
        {
            if (_knownDevices.TryGetValue(update.Id, out var existing))
            {
                if (!isConnected)
                {
                    _knownDevices.TryRemove(update.Id, out _);
                    RuntimeLog.Log(LogTag, $"Device disconnected (update): {existing.Name}");
                    _debouncer.Debounce(() => DeviceDisconnected?.Invoke(this, existing));
                }
            }
            else if (isConnected)
            {
                var info = new BluetoothDeviceInfo
                {
                    Id = update.Id,
                    Name = ExtractNameFromId(update.Id),
                    DeviceType = BluetoothDeviceType.Unknown
                };
                _knownDevices[update.Id] = info;
                RuntimeLog.Log(LogTag, $"Device connected (update): {info.Name}");
                _debouncer.Debounce(() => DeviceConnected?.Invoke(this, info));
            }
        }
    }

    private void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_knownDevices.TryRemove(update.Id, out var removed))
        {
            RuntimeLog.Log(LogTag, $"Device removed: {removed.Name}");
            _debouncer.Debounce(() => DeviceDisconnected?.Invoke(this, removed));
        }
    }

    private void Watcher_EnumerationCompleted(DeviceWatcher sender, object args)
    {
        _isInitialEnumerationComplete = true;
        RuntimeLog.Log(LogTag, $"Initial enumeration complete. {_knownDevices.Count} device(s) connected.");
    }

    private static void Watcher_Stopped(DeviceWatcher sender, object args)
    {
        RuntimeLog.Log(LogTag, "DeviceWatcher stopped");
    }

    private static BluetoothDeviceInfo? CreateDeviceInfo(DeviceInformation device)
    {
        if (string.IsNullOrWhiteSpace(device.Name))
            return null;

        var deviceType = DetectDeviceType(device.Name);

        return new BluetoothDeviceInfo
        {
            Id = device.Id,
            Name = device.Name,
            DeviceType = deviceType
        };
    }
    public static BluetoothDeviceType DetectDeviceType(string name)
    {
        var lower = name.ToLowerInvariant();

        if (lower.Contains("airpods") || lower.Contains("buds") || lower.Contains("earbuds") ||
            lower.Contains("earpods") || lower.Contains("wf-") || lower.Contains("wh-") ||
            lower.Contains("headphone") || lower.Contains("earphone") || lower.Contains("airdots") ||
            lower.Contains("freebuds") || lower.Contains("galaxy buds") || lower.Contains("jabra") ||
            lower.Contains("beats") || lower.Contains("bose") || lower.Contains("sennheiser") ||
            lower.Contains("sony wf") || lower.Contains("sony wh"))
            return BluetoothDeviceType.Headphones;

        if (lower.Contains("speaker") || lower.Contains("soundbar") || lower.Contains("jbl") ||
            lower.Contains("marshall") || lower.Contains("harman") || lower.Contains("sonos") ||
            lower.Contains("ue boom") || lower.Contains("flip"))
            return BluetoothDeviceType.Speaker;

        if (lower.Contains("keyboard") || lower.Contains("keychron") || lower.Contains("k380") ||
            lower.Contains("mx keys"))
            return BluetoothDeviceType.Keyboard;

        if (lower.Contains("mouse") || lower.Contains("mx master") || lower.Contains("mx anywhere") ||
            lower.Contains("trackpad") || lower.Contains("magic mouse"))
            return BluetoothDeviceType.Mouse;

        if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("xbox") ||
            lower.Contains("dualsense") || lower.Contains("dualshock") || lower.Contains("pro controller") ||
            lower.Contains("joy-con"))
            return BluetoothDeviceType.GameController;

        if (lower.Contains("iphone") || lower.Contains("galaxy") || lower.Contains("pixel") ||
            lower.Contains("phone") || lower.Contains("oneplus") || lower.Contains("xiaomi"))
            return BluetoothDeviceType.Phone;

        return BluetoothDeviceType.Unknown;
    }

    private static string ExtractNameFromId(string id)
    {
        var parts = id.Split(IdSeparators);
        return parts.Length > 1 ? parts[^1] : id;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_watcher != null)
        {
            _watcher.Added -= Watcher_Added;
            _watcher.Updated -= Watcher_Updated;
            _watcher.Removed -= Watcher_Removed;
            _watcher.EnumerationCompleted -= Watcher_EnumerationCompleted;
            _watcher.Stopped -= Watcher_Stopped;
            _watcher = null;
        }

        _debouncer.Dispose();
        _knownDevices.Clear();
    }
}
public sealed class BluetoothDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public BluetoothDeviceType DeviceType { get; set; } = BluetoothDeviceType.Unknown;
}
public enum BluetoothDeviceType
{
    Unknown,
    Headphones,
    Speaker,
    Keyboard,
    Mouse,
    GameController,
    Phone
}
