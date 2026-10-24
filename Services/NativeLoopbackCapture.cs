using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>
/// Loopback capture of a render endpoint (Native/Services/LoopbackCapture.cpp). Data arrives on the native
/// capture thread; dispose from any other thread, never from inside the callbacks.
/// </summary>
internal sealed unsafe partial class NativeLoopbackCapture : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int NativeBufferTooSmall = -2;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnLoopbackFormat
    {
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;
        public byte IsFloat;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_loopback_start", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeStart(
        string deviceId, int deviceIdLength, nint onData, nint onStopped, long context,
        out long handle, out VnLoopbackFormat format, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_loopback_stop")]
    private static partial int NativeStop(long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_loopback_get_device")]
    private static partial int NativeGetDevice(long handle, char* buffer, int capacity, out int length, out byte active);

    [LibraryImport(NativeDll, EntryPoint = "vn_loopback_resolve_device_id", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeResolveDeviceId(string requested, int requestedLength, char* buffer, int capacity, out int length);

    private readonly Action<byte[], int> _onData;
    private readonly Action _onStopped;
    private GCHandle _self;
    private long _handle;
    private byte[] _buffer = new byte[16384];

    private NativeLoopbackCapture(Action<byte[], int> onData, Action onStopped)
    {
        _onData = onData;
        _onStopped = onStopped;
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
        capture._self = GCHandle.Alloc(capture);
        delegate* unmanaged[Stdcall]<long, byte*, int, void> data = &OnNativeData;
        delegate* unmanaged[Stdcall]<long, int, void> stopped = &OnNativeStopped;
        string requested = requestedDeviceId ?? string.Empty;
        if (NativeStart(requested, requested.Length, (nint)data, (nint)stopped, GCHandle.ToIntPtr(capture._self),
                out long handle, out VnLoopbackFormat format, out hresult) != 0)
        {
            capture._self.Free();
            return null;
        }

        capture._handle = handle;
        capture.SampleRate = format.SampleRate;
        capture.Channels = format.Channels;
        capture.BitsPerSample = format.BitsPerSample;
        capture.IsFloat = format.IsFloat != 0;
        capture.DeviceId = capture.ReadDeviceId(out _);
        return capture;
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
        if (status == 0) return new string(stack, 0, length);
        if (status != NativeBufferTooSmall || length <= 0 || length > 1 << 15) return null;
        char[] heap = new char[length];
        fixed (char* pinned = heap)
            return NativeResolveDeviceId(requested, requested.Length, pinned, length, out int written) == 0 ? new string(pinned, 0, written) : null;
    }

    private string ReadDeviceId(out bool active)
    {
        active = false;
        if (_handle == 0) return string.Empty;
        char* stack = stackalloc char[256];
        int status = NativeGetDevice(_handle, stack, 256, out int length, out byte isActive);
        active = isActive != 0;
        if (status == 0) return new string(stack, 0, length);
        if (status != NativeBufferTooSmall || length <= 0 || length > 1 << 15) return string.Empty;
        char[] heap = new char[length];
        fixed (char* pinned = heap)
            return NativeGetDevice(_handle, pinned, length, out int written, out _) == 0 ? new string(pinned, 0, written) : string.Empty;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnNativeData(long context, byte* data, int bytes)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)context).Target is not NativeLoopbackCapture capture || bytes <= 0) return;
            if (capture._buffer.Length < bytes) capture._buffer = new byte[bytes];
            new ReadOnlySpan<byte>(data, bytes).CopyTo(capture._buffer);
            capture._onData(capture._buffer, bytes);
        }
        catch (Exception)
        {
            // Exceptions must not unwind into the native capture thread.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnNativeStopped(long context, int hresult)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)context).Target is NativeLoopbackCapture capture) capture._onStopped();
        }
        catch (Exception)
        {
            // Exceptions must not unwind into the native capture thread.
        }
    }

    public void Dispose()
    {
        long handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) NativeStop(handle);
        if (_self.IsAllocated) _self.Free();
    }
}
