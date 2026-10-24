using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VNotch.Services;

internal interface IVolumeEndpoint : IDisposable
{
    float Volume { get; set; }
    bool IsMuted { get; set; }
}

internal interface IVolumeEndpointSource : IDisposable
{
    event Action? DefaultDeviceChanged;
    IVolumeEndpoint OpenDefault();
}

public sealed partial class VolumeService : IVolumeService
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_service_clamp_scalar(
        float volume,
        out float outClamped);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_service_is_target_endpoint(
        int dataFlow,
        int role,
        out byte outIsTarget);

    private const string LogCategory = "VOLUME";
    private readonly object _sync = new();
    private readonly IVolumeEndpointSource _source;
    private IVolumeEndpoint? _endpoint;
    private int _deviceVersion;
    private int _endpointVersion;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_sync) return EnsureInitializedLocked();
        }
    }

    public VolumeService() : this(new CoreAudioVolumeEndpointSource()) { }

    internal VolumeService(IVolumeEndpointSource source)
    {
        _source = source;
        _source.DefaultDeviceChanged += OnDefaultDeviceChanged;
        RefreshDefaultDevice();
    }

    // WASAPI callbacks must not release devices or unregister notifications. Defer
    // that work until the next volume operation, under the normal service lock.
    private void OnDefaultDeviceChanged() => Interlocked.Increment(ref _deviceVersion);

    public bool RefreshDefaultDevice()
    {
        lock (_sync) return InitializeLocked();
    }

    private bool InitializeLocked()
    {
        if (_disposed) return false;
        ReleaseEndpointLocked();
        int version = Volatile.Read(ref _deviceVersion);
        try
        {
            _endpoint = _source.OpenDefault();
            _endpointVersion = version;
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"Init error: {ex.Message}");
            return false;
        }
    }

    public float GetVolume()
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return 0.5f;
            try { return _endpoint!.Volume; }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogCategory, $"GetVolume error: {ex.Message}");
                ReleaseEndpointLocked();
                return 0.5f;
            }
        }
    }

    public bool SetVolume(float volume)
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return false;
            try
            {
                _endpoint!.Volume = vn_volume_service_clamp_scalar(volume, out float clamped) == 0
                    ? clamped
                    : Math.Clamp(volume, 0f, 1f);
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogCategory, $"SetVolume error: {ex.Message}");
                ReleaseEndpointLocked();
                return false;
            }
        }
    }

    public bool GetMute()
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return false;
            try { return _endpoint!.IsMuted; }
            catch (Exception ex)
            {
                RuntimeLog.Error("VOLUME-GETMUTE", ex.ToString());
                ReleaseEndpointLocked();
                return false;
            }
        }
    }

    public void SetMute(bool mute)
    {
        lock (_sync)
        {
            if (!EnsureInitializedLocked()) return;
            try { _endpoint!.IsMuted = mute; }
            catch (Exception ex)
            {
                RuntimeLog.Error("VOLUME-SETMUTE", ex.ToString());
                ReleaseEndpointLocked();
            }
        }
    }

    public void ToggleMute()
    {
        lock (_sync) SetMute(!GetMute());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _source.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            ReleaseEndpointLocked();
            _source.Dispose();
        }
    }

    private bool EnsureInitializedLocked() => !_disposed &&
        ((_endpoint != null && _endpointVersion == Volatile.Read(ref _deviceVersion)) || InitializeLocked());

    private void ReleaseEndpointLocked()
    {
        var endpoint = _endpoint;
        _endpoint = null;
        try { endpoint?.Dispose(); }
        catch (Exception ex) { RuntimeLog.Log(LogCategory, $"Endpoint cleanup error: {ex.Message}"); }
    }

    private sealed class CoreAudioVolumeEndpointSource : IVolumeEndpointSource, IMMNotificationClient
    {
        private MMDeviceEnumerator? _enumerator;
        public event Action? DefaultDeviceChanged;

        public IVolumeEndpoint OpenDefault()
        {
            if (_enumerator == null)
            {
                var enumerator = new MMDeviceEnumerator();
                try
                {
                    Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(this));
                    _enumerator = enumerator;
                }
                catch
                {
                    enumerator.Dispose();
                    throw;
                }
            }

            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            try { return new CoreAudioVolumeEndpoint(device); }
            catch
            {
                device.Dispose();
                throw;
            }
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            bool isTarget = vn_volume_service_is_target_endpoint((int)flow, (int)role, out byte outTarget) == 0
                ? outTarget != 0
                : (flow == DataFlow.Render && role == Role.Multimedia);
            if (isTarget) DefaultDeviceChanged?.Invoke();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

        public void Dispose()
        {
            if (_enumerator == null) return;
            try { _enumerator.UnregisterEndpointNotificationCallback(this); }
            catch (Exception ex) { RuntimeLog.Log(LogCategory, $"Notification cleanup error: {ex.Message}"); }
            finally
            {
                _enumerator.Dispose();
                _enumerator = null;
            }
        }
    }

    private sealed class CoreAudioVolumeEndpoint : IVolumeEndpoint
    {
        private readonly MMDevice _device;
        private readonly AudioEndpointVolume _volume;

        public CoreAudioVolumeEndpoint(MMDevice device)
        {
            _device = device;
            _volume = device.AudioEndpointVolume;
        }

        public float Volume { get => _volume.MasterVolumeLevelScalar; set => _volume.MasterVolumeLevelScalar = value; }
        public bool IsMuted { get => _volume.Mute; set => _volume.Mute = value; }
        public void Dispose() => _device.Dispose();
    }
}
