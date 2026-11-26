using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VNotch.Interop;

namespace VNotch.Services;

/// <summary>
/// Loopback capture of a render endpoint (Native/Services/LoopbackCapture.cpp). Data arrives on the native
/// capture thread; SafeHandle guarantees stop and callback root cleanup even if Dispose is missed.
/// </summary>
internal sealed unsafe partial class NativeLoopbackCapture : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnLoopbackFormat
    {
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;
        public byte IsFloat;
    }

    private sealed class CaptureHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private GCHandle _callbackRoot;

        internal CaptureHandle(long handleValue, GCHandle callbackRoot)
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
                NativeStop(nativeHandle);
            }

            if (_callbackRoot.IsAllocated)
            {
                _callbackRoot.Free();
            }

            return true;
        }
    }

    [LibraryImport(NativeAbi.LibraryName, EntryPoint = "vn_loopback_start", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeStart(
        string deviceId, int deviceIdLength, nint onData, nint onStopped, long context,
        out long handle, out VnLoopbackFormat format, out int hresult);

    [LibraryImport(NativeAbi.LibraryName, EntryPoint = "vn_loopback_start_levels", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeStartLevels(string deviceId, int length, nint levels, nint stopped, long context,
        out long handle, out VnLoopbackFormat format, out int hresult);

    [LibraryImport(NativeAbi.LibraryName, EntryPoint = "vn_loopback_stop")]
    private static partial int NativeStop(long handle);

    [LibraryImport(NativeAbi.LibraryName, EntryPoint = "vn_loopback_get_device")]
    private static partial int NativeGetDevice(long handle, char* buffer, int capacity, out int length, out byte active);

    [LibraryImport(NativeAbi.LibraryName, EntryPoint = "vn_loopback_resolve_device_id", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeResolveDeviceId(string requested, int requestedLength, char* buffer, int capacity, out int length);

    internal delegate void LevelsCallback(ReadOnlySpan<float> levels, float beat);
    private readonly Action<byte[], int>? _onData;
    private readonly LevelsCallback? _onLevels;
    private readonly Action _onStopped;
    private CaptureHandle? _captureHandle;
    private int _activeCallbackThreadId;
    private int _disposed;
    private byte[] _buffer = [];

    private NativeLoopbackCapture(Action<byte[], int> onData, Action onStopped)
    {
        _buffer = new byte[16384];
        _onData = onData;
        _onStopped = onStopped;
    }

    private NativeLoopbackCapture(LevelsCallback onLevels, Action onStopped)
    {
        _onLevels = onLevels;
        _onStopped = onStopped;
    }

    internal static NativeLoopbackCapture? StartAnalyzed(string requestedDeviceId, LevelsCallback onLevels,
        Action onStopped, out int hresult)
    {
        var capture = new NativeLoopbackCapture(onLevels, onStopped);
        delegate* unmanaged[Stdcall]<long, float*, float, void> levels = &OnNativeLevels;
        return capture.StartCore(requestedDeviceId, (nint)levels, analyzed: true, out hresult);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnNativeLevels(long context, float* values, float beat)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is not NativeLoopbackCapture capture)
        {
            return;
        }

        if (Volatile.Read(ref capture._disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref capture._activeCallbackThreadId, Environment.CurrentManagedThreadId);
        try
        {
            capture._onLevels?.Invoke(new ReadOnlySpan<float>(values, 5), beat);
        }
        catch (Exception)
        {
            // Never unwind through the native capture thread.
        }
        finally
        {
            Volatile.Write(ref capture._activeCallbackThreadId, 0);
        }
    }

    internal int SampleRate { get; private set; }

    internal int Channels { get; private set; }

    internal int BitsPerSample { get; private set; }

    internal bool IsFloat { get; private set; }

    /// <summary>Id of the endpoint being captured.</summary>
    internal string DeviceId { get; private set; } = string.Empty;

    /// <summary>
    /// Starts capturing the requested endpoint (empty or inactive selects the default render endpoint).
    /// Returns null with the failing HRESULT when no endpoint can be captured.
    /// </summary>
    internal static NativeLoopbackCapture? Start(string requestedDeviceId, Action<byte[], int> onData, Action onStopped, out int hresult)
    {
        var capture = new NativeLoopbackCapture(onData, onStopped);
        delegate* unmanaged[Stdcall]<long, byte*, int, void> data = &OnNativeData;
        return capture.StartCore(requestedDeviceId, (nint)data, analyzed: false, out hresult);
    }

    private NativeLoopbackCapture? StartCore(string requestedDeviceId, nint callback, bool analyzed, out int hresult)
    {
        using var pending = new PendingCallbackHandle(this);
        delegate* unmanaged[Stdcall]<long, int, void> stopped = &OnNativeStopped;
        string requested = requestedDeviceId ?? string.Empty;
        int status = analyzed
            ? NativeStartLevels(requested, requested.Length, callback, (nint)stopped, pending.Context,
                out long handle, out VnLoopbackFormat format, out hresult)
            : NativeStart(requested, requested.Length, callback, (nint)stopped, pending.Context,
                out handle, out format, out hresult);
        if (status != (int)NativeStatus.Ok)
        {
            return null;
        }

        _captureHandle = new CaptureHandle(handle, pending.Transfer());
        try
        {
            SampleRate = format.SampleRate;
            Channels = format.Channels;
            BitsPerSample = format.BitsPerSample;
            IsFloat = format.IsFloat != 0;
            DeviceId = ReadDeviceId(out _);
            return this;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>True while the captured endpoint is still active.</summary>
    internal bool IsDeviceActive()
    {
        ReadDeviceId(out bool active);
        return active;
    }

    /// <summary>Id of the endpoint a capture would use for the request, or null when none exists.</summary>
    internal static string? ResolveDeviceId(string requestedDeviceId)
    {
        string requested = requestedDeviceId ?? string.Empty;
        char* stack = stackalloc char[256];
        int status = NativeResolveDeviceId(requested, requested.Length, stack, 256, out int length);
        if (status == (int)NativeStatus.Ok)
        {
            return new string(stack, 0, length);
        }

        if (status != (int)NativeStatus.BufferTooSmall || length <= 0 || length > 1 << 15)
        {
            return null;
        }

        char[] heap = new char[length];
        fixed (char* pinned = heap)
        {
            return NativeResolveDeviceId(requested, requested.Length, pinned, length, out int written) == (int)NativeStatus.Ok
                ? new string(pinned, 0, written)
                : null;
        }
    }

    private string ReadDeviceId(out bool active)
    {
        active = false;
        CaptureHandle? handle = Volatile.Read(ref _captureHandle);
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return string.Empty;
        }

        bool addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            long nativeHandle = handle.DangerousGetHandle().ToInt64();
            char* stack = stackalloc char[256];
            int status = NativeGetDevice(nativeHandle, stack, 256, out int length, out byte isActive);
            active = isActive != 0;
            if (status == (int)NativeStatus.Ok)
            {
                return new string(stack, 0, length);
            }

            if (status != (int)NativeStatus.BufferTooSmall || length <= 0 || length > 1 << 15)
            {
                return string.Empty;
            }

            char[] heap = new char[length];
            fixed (char* pinned = heap)
            {
                return NativeGetDevice(nativeHandle, pinned, length, out int written, out _) == (int)NativeStatus.Ok
                    ? new string(pinned, 0, written)
                    : string.Empty;
            }
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnNativeData(long context, byte* data, int bytes)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is not NativeLoopbackCapture capture || bytes <= 0)
        {
            return;
        }

        if (Volatile.Read(ref capture._disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref capture._activeCallbackThreadId, Environment.CurrentManagedThreadId);
        try
        {
            if (capture._buffer.Length < bytes)
            {
                capture._buffer = new byte[bytes];
            }

            new ReadOnlySpan<byte>(data, bytes).CopyTo(capture._buffer);
            capture._onData?.Invoke(capture._buffer, bytes);
        }
        catch (Exception)
        {
            // Exceptions must not unwind into the native capture thread.
        }
        finally
        {
            Volatile.Write(ref capture._activeCallbackThreadId, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnNativeStopped(long context, int hresult)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is not NativeLoopbackCapture capture)
        {
            return;
        }

        if (Volatile.Read(ref capture._disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref capture._activeCallbackThreadId, Environment.CurrentManagedThreadId);
        try
        {
            capture._onStopped();
        }
        catch (Exception)
        {
            // Exceptions must not unwind into the native capture thread.
        }
        finally
        {
            Volatile.Write(ref capture._activeCallbackThreadId, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CaptureHandle? handle = Interlocked.Exchange(ref _captureHandle, null);
        if (handle is null)
        {
            return;
        }

        if (Volatile.Read(ref _activeCallbackThreadId) == Environment.CurrentManagedThreadId)
        {
            // NativeStop cannot join the capture thread from inside its own callback; signal stop now
            // and finish handle teardown on the thread pool so the GCHandle outlives the native thread.
            long rawHandle = handle.DangerousGetHandle().ToInt64();
            if (rawHandle > 0)
            {
                NativeStop(rawHandle);
            }

            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Dispose(), handle, preferLocal: false);
            return;
        }

        handle.Dispose();
    }
}
