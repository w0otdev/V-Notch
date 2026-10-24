using System.Runtime.InteropServices;

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
    private static partial int vn_volume_source_create(nint callback, long context, out long handle, out int hresult);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_source_destroy(long handle);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_endpoint_open(long source, out long handle, out int hresult);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_endpoint_close(long handle);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_endpoint_get(long handle, out float scalar, out byte muted, out int hresult);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_endpoint_set_volume(long handle, float scalar, out int hresult);

    [LibraryImport(NativeDll)]
    private static partial int vn_volume_endpoint_set_mute(long handle, byte muted, out int hresult);
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

    // Core Audio calls live in Native/Services/CoreAudioVolume.cpp; this wrapper adapts them to the endpoint seam.
    private sealed unsafe class CoreAudioVolumeEndpointSource : IVolumeEndpointSource
    {
        private const int EFail = unchecked((int)0x80004005);
        private long _source;
        private GCHandle _self;

        public event Action? DefaultDeviceChanged;

        public IVolumeEndpoint OpenDefault()
        {
            if (_source == 0)
            {
                _self = GCHandle.Alloc(this);
                delegate* unmanaged[Stdcall]<long, void> callback = &OnDeviceChanged;
                if (vn_volume_source_create((nint)callback, GCHandle.ToIntPtr(_self), out long source, out int createHr) != 0)
                {
                    _self.Free();
                    Marshal.ThrowExceptionForHR(createHr < 0 ? createHr : EFail);
                }

                _source = source;
            }

            if (vn_volume_endpoint_open(_source, out long endpoint, out int openHr) != 0)
                Marshal.ThrowExceptionForHR(openHr < 0 ? openHr : EFail);
            return new CoreAudioVolumeEndpoint(endpoint);
        }

        // Runs on a Core Audio thread: record the change only, the service re-opens on its next call.
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static void OnDeviceChanged(long context)
        {
            try
            {
                if (GCHandle.FromIntPtr((IntPtr)context).Target is CoreAudioVolumeEndpointSource source)
                    source.DefaultDeviceChanged?.Invoke();
            }
            catch (Exception)
            {
                // Exceptions must not unwind into the native notification thread.
            }
        }

        public void Dispose()
        {
            if (_source == 0) return;
            vn_volume_source_destroy(_source);
            _source = 0;
            if (_self.IsAllocated) _self.Free();
        }
    }

    private sealed class CoreAudioVolumeEndpoint(long handle) : IVolumeEndpoint
    {
        public float Volume
        {
            get
            {
                if (vn_volume_endpoint_get(handle, out float scalar, out _, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
                return scalar;
            }
            set
            {
                if (vn_volume_endpoint_set_volume(handle, value, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        public bool IsMuted
        {
            get
            {
                if (vn_volume_endpoint_get(handle, out _, out byte muted, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
                return muted != 0;
            }
            set
            {
                if (vn_volume_endpoint_set_mute(handle, value ? (byte)1 : (byte)0, out int hr) != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        public void Dispose() => vn_volume_endpoint_close(handle);
    }
}