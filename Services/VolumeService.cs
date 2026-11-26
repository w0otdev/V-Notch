using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VNotch.Interop;

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
    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_service_clamp_scalar(
        float volume,
        out float outClamped);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_source_create(nint callback, long context, out long handle, out int hresult);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_source_destroy(long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_open(VolumeSourceHandle source, out VolumeEndpointHandle handle, out int hresult);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_close(long handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_get(VolumeEndpointHandle handle, out float scalar, out byte muted, out int hresult);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_set_volume(VolumeEndpointHandle handle, float scalar, out int hresult);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_volume_endpoint_set_mute(VolumeEndpointHandle handle, byte muted, out int hresult);

    private sealed class VolumeSourceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private GCHandle _callbackRoot;

        internal VolumeSourceHandle(long handleValue, GCHandle callbackRoot)
            : base(true)
        {
            SetHandle((nint)handleValue);
            _callbackRoot = callbackRoot;
        }

        protected override bool ReleaseHandle()
        {
            long nativeHandle = handle.ToInt64();
            if (nativeHandle > 0)
            {
                vn_volume_source_destroy(nativeHandle);
            }

            if (_callbackRoot.IsAllocated)
            {
                _callbackRoot.Free();
            }

            return true;
        }
    }

    private sealed class VolumeEndpointHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public VolumeEndpointHandle() : base(true) { }

        protected override bool ReleaseHandle() => vn_volume_endpoint_close(handle.ToInt64()) == (int)NativeStatus.Ok;
    }
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
        private VolumeSourceHandle? _sourceHandle;
        private int _activeCallbackThreadId;
        private int _disposed;
        private int _disposeDeferred;

        public event Action? DefaultDeviceChanged;

        public IVolumeEndpoint OpenDefault()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            VolumeSourceHandle? sourceHandle = Volatile.Read(ref _sourceHandle);
            if (sourceHandle is null || sourceHandle.IsInvalid || sourceHandle.IsClosed)
            {
                using var pending = new PendingCallbackHandle(this);
                delegate* unmanaged[Stdcall]<long, void> callback = &OnDeviceChanged;
                if (vn_volume_source_create((nint)callback, pending.Context, out long source, out int createHr) != (int)NativeStatus.Ok)
                {
                    Marshal.ThrowExceptionForHR(createHr < 0 ? createHr : EFail);
                }

                sourceHandle = new VolumeSourceHandle(source, pending.Transfer());
                Volatile.Write(ref _sourceHandle, sourceHandle);
            }

            if (vn_volume_endpoint_open(sourceHandle, out VolumeEndpointHandle endpoint, out int openHr) != (int)NativeStatus.Ok)
            {
                Marshal.ThrowExceptionForHR(openHr < 0 ? openHr : EFail);
            }

            return new CoreAudioVolumeEndpoint(endpoint);
        }

        // Runs on a Core Audio thread: record the change only, the service re-opens on its next call.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OnDeviceChanged(long context)
        {
            if (GCHandle.FromIntPtr((IntPtr)context).Target is not CoreAudioVolumeEndpointSource source)
            {
                return;
            }

            if (Volatile.Read(ref source._disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref source._activeCallbackThreadId, Environment.CurrentManagedThreadId);
            try
            {
                source.DefaultDeviceChanged?.Invoke();
            }
            catch (Exception)
            {
                // Exceptions must not unwind into the native notification thread.
            }
            finally
            {
                Volatile.Write(ref source._activeCallbackThreadId, 0);
                if (Volatile.Read(ref source._disposeDeferred) != 0)
                {
                    Interlocked.Exchange(ref source._sourceHandle, null)?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Volatile.Read(ref _activeCallbackThreadId) == Environment.CurrentManagedThreadId)
            {
                Volatile.Write(ref _disposeDeferred, 1);
                return;
            }

            Interlocked.Exchange(ref _sourceHandle, null)?.Dispose();
        }
    }

    private sealed class CoreAudioVolumeEndpoint(VolumeEndpointHandle handle) : IVolumeEndpoint
    {
        public float Volume
        {
            get
            {
                if (vn_volume_endpoint_get(handle, out float scalar, out _, out int hr) != (int)NativeStatus.Ok)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return scalar;
            }
            set
            {
                if (vn_volume_endpoint_set_volume(handle, value, out int hr) != (int)NativeStatus.Ok)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
        }

        public bool IsMuted
        {
            get
            {
                if (vn_volume_endpoint_get(handle, out _, out byte muted, out int hr) != (int)NativeStatus.Ok)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                return muted != 0;
            }
            set
            {
                if (vn_volume_endpoint_set_mute(handle, value ? (byte)1 : (byte)0, out int hr) != (int)NativeStatus.Ok)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
        }

        public void Dispose() => handle.Dispose();
    }
}
