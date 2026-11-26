using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VNotch.Interop;
using Windows.Devices.Enumeration;

namespace VNotch.Services;

public sealed partial class BluetoothMonitorService : IDisposable
{
    private const string NativeDll = NativeAbi.LibraryName;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_bluetooth_monitor_detect_device_type(
        ReadOnlySpan<char> namePtr,
        int nameLen,
        out int outType);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_bluetooth_monitor_extract_name_start(
        ReadOnlySpan<char> idPtr,
        int idLen,
        out int outStart);

    private const string LogTag = "BLUETOOTH";
    internal const int MaxConsecutiveRestarts = 5;
    private const int BaseRestartDelayMs = 500;
    private const int MaxRestartDelayMs = 30_000;

    private DeviceWatcher? _watcher;
    private readonly ConcurrentDictionary<string, BluetoothDeviceInfo> _knownDevices = new();
    private readonly object _deviceChangeLock = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action _restartAction;
    private CancellationTokenSource? _restartCts;
    private int _consecutiveRestarts;
    private volatile bool _disposed;
    private volatile bool _isInitialEnumerationComplete;
    public event EventHandler<BluetoothDeviceInfo>? DeviceConnected;
    public event EventHandler<BluetoothDeviceInfo>? DeviceDisconnected;

    public BluetoothMonitorService() : this(System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
    {
    }

    public BluetoothMonitorService(Dispatcher dispatcher)
        : this(dispatcher, delayAsync: null, restartAction: null)
    {
    }

    internal BluetoothMonitorService(
        Dispatcher dispatcher,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        Action? restartAction)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _delayAsync = delayAsync ?? (static (delay, ct) => Task.Delay(delay, ct));
        _restartAction = restartAction ?? Start;
    }

    public void Start()
    {
        lock (_deviceChangeLock)
        {
            if (_disposed || _watcher != null) return;

            try
            {
                string aqsFilter = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"" +
                                   " AND System.Devices.Aep.IsConnected:=System.StructuredQueryType.Boolean#True";

                string[] requestedProperties = new[]
                {
                    "System.ItemNameDisplay",
                    "System.Devices.Aep.DeviceAddress",
                    "System.Devices.Aep.IsConnected",
                    "System.Devices.Aep.Category"
                };

                _isInitialEnumerationComplete = false;
                _knownDevices.Clear();

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
                StopCore(resetRestartCounter: false);
            }
        }
    }

    public void Stop() => StopCore(resetRestartCounter: true);

    private void StopCore(bool resetRestartCounter)
    {
        DeviceWatcher? watcher;
        lock (_deviceChangeLock)
        {
            CancelPendingRestartLocked();
            if (resetRestartCounter)
                _consecutiveRestarts = 0;

            watcher = DetachWatcherStateLocked();
            if (watcher == null) return;
        }

        DetachAndStopWatcher(watcher);
    }

    private DeviceWatcher? DetachWatcherStateLocked()
    {
        DeviceWatcher? watcher = _watcher;
        _watcher = null;
        _isInitialEnumerationComplete = false;
        _knownDevices.Clear();
        return watcher;
    }

    private void DetachAndStopWatcher(DeviceWatcher watcher)
    {
        try
        {
            watcher.Added -= Watcher_Added;
            watcher.Updated -= Watcher_Updated;
            watcher.Removed -= Watcher_Removed;
            watcher.EnumerationCompleted -= Watcher_EnumerationCompleted;
            watcher.Stopped -= Watcher_Stopped;

            if (watcher.Status == DeviceWatcherStatus.Started ||
                watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "Failed to stop DeviceWatcher");
        }
    }

    private void Watcher_Added(DeviceWatcher sender, DeviceInformation device)
    {
        lock (_deviceChangeLock)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            var info = CreateDeviceInfo(device);
            if (info != null) AddDevice(info);
        }
    }

    internal void AddDevice(BluetoothDeviceInfo info)
    {
        lock (_deviceChangeLock)
        {
            if (_disposed || !_knownDevices.TryAdd(info.Id, info)) return;
            RuntimeLog.Log(LogTag, $"Device connected: {info.Name} ({info.DeviceType})");
            if (_isInitialEnumerationComplete)
                PublishDeviceChange(info, connected: true);
        }
    }

    private void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_deviceChangeLock)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            if (update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connectedObj)
                && connectedObj is bool isConnected)
            {
                if (!isConnected)
                {
                    RemoveDevice(update.Id);
                }
                else if (!_knownDevices.ContainsKey(update.Id))
                {
                    string deviceName = update.Properties.TryGetValue("System.ItemNameDisplay", out var nameObj)
                        && nameObj is string displayName && !string.IsNullOrWhiteSpace(displayName)
                        ? displayName
                        : ExtractNameFromId(update.Id);
                    var info = new BluetoothDeviceInfo
                    {
                        Id = update.Id,
                        Name = deviceName,
                        DeviceType = DetectDeviceType(deviceName)
                    };
                    AddDevice(info);
                }
            }
        }
    }

    private void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_deviceChangeLock)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            RemoveDevice(update.Id);
        }
    }

    internal void RemoveDevice(string id)
    {
        lock (_deviceChangeLock)
        {
            if (_disposed) return;
            if (_knownDevices.TryRemove(id, out var removed))
            {
                RuntimeLog.Log(LogTag, $"Device removed: {removed.Name}");
                if (_isInitialEnumerationComplete)
                    PublishDeviceChange(removed, connected: false);
            }
        }
    }

    private void PublishDeviceChange(BluetoothDeviceInfo info, bool connected)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        var watcher = _watcher;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !_isInitialEnumerationComplete || !ReferenceEquals(watcher, _watcher)) return;
            if (connected) DeviceConnected?.Invoke(this, info);
            else DeviceDisconnected?.Invoke(this, info);
        });
    }

    private void Watcher_EnumerationCompleted(DeviceWatcher sender, object args)
    {
        lock (_deviceChangeLock)
        {
            if (_disposed || sender == null || !ReferenceEquals(sender, _watcher)) return;
            _isInitialEnumerationComplete = true;
            _consecutiveRestarts = 0;
            RuntimeLog.Log(LogTag, $"Initial enumeration complete. {_knownDevices.Count} device(s) connected.");
        }
    }

    private void Watcher_Stopped(DeviceWatcher sender, object args)
    {
        if (_disposed || sender == null || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            DeviceWatcher? stoppedWatcher;
            TimeSpan delay;
            CancellationTokenSource? restartCts;
            lock (_deviceChangeLock)
            {
                if (_disposed || !ReferenceEquals(sender, _watcher)) return;
                stoppedWatcher = DetachWatcherStateLocked();
                if (!TryPrepareRestartLocked(out delay, out restartCts))
                    restartCts = null;
            }

            if (stoppedWatcher != null)
                DetachAndStopWatcher(stoppedWatcher);

            if (restartCts != null)
                ExecuteDelayedRestartAsync(delay, restartCts).SafeFireAndForget(LogTag);
        });
    }

    internal static TimeSpan ComputeRestartDelay(int attempt)
    {
        int exponent = Math.Clamp(attempt, 0, 10);
        int delayMs = Math.Min(MaxRestartDelayMs, BaseRestartDelayMs * (1 << exponent));
        return TimeSpan.FromMilliseconds(delayMs);
    }

    internal bool TryScheduleRestartForTest()
    {
        TimeSpan delay;
        CancellationTokenSource? restartCts;
        lock (_deviceChangeLock)
        {
            if (_disposed || !TryPrepareRestartLocked(out delay, out restartCts))
                return false;
        }

        ExecuteDelayedRestartAsync(delay, restartCts!).SafeFireAndForget(LogTag);
        return true;
    }

    private bool TryPrepareRestartLocked(out TimeSpan delay, out CancellationTokenSource? restartCts)
    {
        if (_disposed || _consecutiveRestarts >= MaxConsecutiveRestarts)
        {
            if (!_disposed && _consecutiveRestarts == MaxConsecutiveRestarts)
                RuntimeLog.Warn(LogTag, $"DeviceWatcher stopped {MaxConsecutiveRestarts} consecutive times; pausing auto-restart.");

            delay = TimeSpan.Zero;
            restartCts = null;
            return false;
        }

        delay = ComputeRestartDelay(_consecutiveRestarts);
        _consecutiveRestarts++;
        CancelPendingRestartLocked();
        restartCts = new CancellationTokenSource();
        _restartCts = restartCts;
        return true;
    }

    private void CancelPendingRestartLocked()
    {
        var cts = _restartCts;
        _restartCts = null;
        if (cts == null) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* Already disposed by ExecuteDelayedRestartAsync. */ }
    }

    private async Task ExecuteDelayedRestartAsync(TimeSpan delay, CancellationTokenSource restartCts)
    {
        try
        {
            await _delayAsync(delay, restartCts.Token).ConfigureAwait(false);
            if (_disposed || _dispatcher.HasShutdownStarted) return;

            await _dispatcher.InvokeAsync(() =>
            {
                lock (_deviceChangeLock)
                {
                    if (_disposed || !ReferenceEquals(_restartCts, restartCts) || restartCts.IsCancellationRequested)
                        return;

                    _restartCts = null;
                }

                _restartAction();
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Explicit Stop() or Dispose() canceled the scheduled restart.
        }
        finally
        {
            lock (_deviceChangeLock)
            {
                if (ReferenceEquals(_restartCts, restartCts))
                    _restartCts = null;
            }

            restartCts.Dispose();
        }
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
        if (string.IsNullOrEmpty(name)) return BluetoothDeviceType.Unknown;
        ReadOnlySpan<char> span = name.AsSpan();
        vn_bluetooth_monitor_detect_device_type(span, span.Length, out int type);
        return (BluetoothDeviceType)type;
    }

    private static string ExtractNameFromId(string id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        ReadOnlySpan<char> span = id.AsSpan();
        vn_bluetooth_monitor_extract_name_start(span, span.Length, out int start);
        return start > 0 && start <= id.Length ? id[start..] : id;
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
