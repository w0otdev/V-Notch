using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// Desktop backdrop source for Liquid Glass. The Magnification API host window, pump thread, scaling
/// callback and frame buffer live in VNotch.Native (Native/Controllers/MagnifierCapture.cpp); this class
/// tracks the shared instance, forwards exclusion windows and logs native init failures.
/// </summary>
public sealed partial class MagnifierCaptureSource : IDisposable
{
    private const string LogTag = "LIQUIDGLASS";
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_create")]
    private static partial int NativeCreate(out int handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_initialize")]
    private static partial int NativeInitialize(int handle, long excludeHwnd, out byte ready, out int stage);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_stop")]
    private static partial int NativeStop(int handle, out byte exited);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_destroy")]
    private static partial int NativeDestroy(int handle, out byte released);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_set_capture_enabled")]
    private static partial int NativeSetCaptureEnabled(int handle, byte enabled);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_get_state")]
    private static partial int NativeGetState(int handle, out byte ready, out byte running);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_add_exclude")]
    private static partial int NativeAddExclude(int handle, long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_remove_exclude")]
    private static partial int NativeRemoveExclude(int handle, long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_capture_into")]
    private static partial int NativeCaptureInto(int handle, int x, int y, int width, int height, IntPtr destination, out byte ok);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_has_usable_frame")]
    private static partial int NativeHasUsableFrame(int handle, int x, int y, int width, int height, out byte usable);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_wait_unfiltered_frame")]
    private static partial int NativeWaitUnfilteredFrame(int handle, uint timeoutMs, out byte received);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_debug_status")]
    private static unsafe partial int NativeDebugStatus(int handle, int x, int y, int width, int height, char* buffer, int capacity, out int length);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_is_complete_frame")]
    private static partial int NativeIsCompleteFrame(
        int requestedWidth, int requestedHeight, int receivedWidth, int receivedHeight, int receivedStride, int bufferLength, out byte complete);

    [LibraryImport(NativeDll, EntryPoint = "vn_magcap_test_install_frame")]
    private static unsafe partial int NativeTestInstallFrame(
        int handle, byte ready, byte running, int width, int height, byte* pixels, int pixelBytes, long frameAgeMs);

    private static readonly string[] InitFailureMessages =
    [
        "",
        "MagInitialize failed.",
        "Mag host create failed.",
        "Mag control create failed.",
        "MagSetImageScalingCallback unsupported.",
    ];

    private static readonly object _sharedSync = new();
    private static MagnifierCaptureSource? _sharedInstance;
    private static int _sharedRefCount;
    // Replaced instances that earlier holders may still reference; disposed once no holder remains.
    private static readonly List<MagnifierCaptureSource> _retiredInstances = new();

    public static MagnifierCaptureSource AcquireShared(IntPtr excludeHwnd)
    {
        lock (_sharedSync)
        {
            if (_sharedInstance == null || !_sharedInstance.IsRunning || !_sharedInstance.IsReady)
            {
                RetireOrDisposeSharedInstance();
                _sharedInstance = new MagnifierCaptureSource();
                _sharedInstance.Initialize(excludeHwnd);
            }
            else if (excludeHwnd != IntPtr.Zero)
            {
                _sharedInstance.AddExcludeHwnd(excludeHwnd);
            }
            _sharedRefCount++;
            _sharedInstance.SetCaptureEnabled(true);
            return _sharedInstance;
        }
    }

    // Caller must hold _sharedSync.
    private static void RetireOrDisposeSharedInstance()
    {
        MagnifierCaptureSource? old = _sharedInstance;
        _sharedInstance = null;
        if (old == null) return;

        if (_sharedRefCount == 0)
        {
            DisposeQuietly(old);
            return;
        }

        // Other holders still use this instance: stop its pump thread but keep the native object alive
        // so their CaptureInto calls fail softly instead of throwing.
        old.StopPumpThread();
        if (_retiredInstances.Count >= 4)
        {
            DisposeQuietly(_retiredInstances[0]);
            _retiredInstances.RemoveAt(0);
        }
        _retiredInstances.Add(old);
    }

    // Caller must hold _sharedSync.
    private static void DisposeRetiredInstances()
    {
        foreach (var retired in _retiredInstances)
            DisposeQuietly(retired);
        _retiredInstances.Clear();
    }

    private static void DisposeQuietly(MagnifierCaptureSource source)
    {
        try
        {
            source.Dispose();
        }
        catch (Exception)
        {
            // Ignore errors when disposing a replaced or shut down shared instance.
        }
    }

    public static void ReleaseShared(IntPtr excludeHwnd)
    {
        lock (_sharedSync)
        {
            if (excludeHwnd != IntPtr.Zero && _sharedInstance != null)
            {
                _sharedInstance.RemoveExcludeHwnd(excludeHwnd);
            }
            _sharedRefCount = Math.Max(0, _sharedRefCount - 1);
            if (_sharedRefCount != 0) return;

            DisposeRetiredInstances();
            _sharedInstance?.SetCaptureEnabled(false);
        }
    }

    public static void ShutdownShared()
    {
        lock (_sharedSync)
        {
            if (_sharedInstance != null)
                DisposeQuietly(_sharedInstance);
            DisposeRetiredInstances();
            _sharedInstance = null;
            _sharedRefCount = 0;
        }
    }

    public static bool PrewarmUnfilteredDesktopFrame(TimeSpan timeout)
    {
        MagnifierCaptureSource source = AcquireShared(IntPtr.Zero);
        try
        {
            uint timeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            return source._handle > 0 &&
                   NativeWaitUnfilteredFrame(source._handle, timeoutMs, out byte received) == 0 && received != 0;
        }
        finally
        {
            ReleaseShared(IntPtr.Zero);
        }
    }

    internal static bool IsCompleteFrame(
        int requestedWidth,
        int requestedHeight,
        int receivedWidth,
        int receivedHeight,
        int receivedStride,
        int bufferLength) =>
        NativeIsCompleteFrame(requestedWidth, requestedHeight, receivedWidth, receivedHeight, receivedStride, bufferLength, out byte complete) == 0 &&
        complete != 0;

    private int _handle;
    private int _disposed;
    private readonly object _providerLock = new();
    private readonly List<Func<IntPtr>> _dynamicExcludeProviders = new();
    private readonly List<IntPtr> _dynamicExcludeLastHwnds = new();

    public MagnifierCaptureSource()
    {
        if (NativeCreate(out int handle) == 0) _handle = handle;
    }

    public bool IsReady => _handle > 0 && NativeGetState(_handle, out byte ready, out _) == 0 && ready != 0;

    private bool IsRunning => _handle > 0 && NativeGetState(_handle, out _, out byte running) == 0 && running != 0;

    public void AddExcludeHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _handle <= 0) return;
        NativeAddExclude(_handle, hwnd);
    }

    public void RemoveExcludeHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _handle <= 0) return;
        lock (_providerLock)
        {
            for (int i = 0; i < _dynamicExcludeLastHwnds.Count; i++)
            {
                if (_dynamicExcludeLastHwnds[i] == hwnd)
                    _dynamicExcludeLastHwnds[i] = IntPtr.Zero;
            }
        }
        NativeRemoveExclude(_handle, hwnd);
    }

    public void RegisterExcludeHwndProvider(Func<IntPtr>? provider)
    {
        if (provider == null) return;
        lock (_providerLock)
        {
            if (!_dynamicExcludeProviders.Contains(provider))
            {
                _dynamicExcludeProviders.Add(provider);
                _dynamicExcludeLastHwnds.Add(IntPtr.Zero);
            }
        }
    }

    public void UnregisterExcludeHwndProvider(Func<IntPtr>? provider)
    {
        if (provider == null) return;
        lock (_providerLock)
        {
            int idx = _dynamicExcludeProviders.IndexOf(provider);
            if (idx >= 0)
            {
                _dynamicExcludeProviders.RemoveAt(idx);
                _dynamicExcludeLastHwnds.RemoveAt(idx);
            }
        }
    }

    // Providers are polled on each capture or probe; windows they report join the native exclusion list.
    private void PollDynamicExcludeProviders()
    {
        lock (_providerLock)
        {
            for (int i = 0; i < _dynamicExcludeProviders.Count; i++)
            {
                IntPtr hwnd;
                try { hwnd = _dynamicExcludeProviders[i](); }
                catch { continue; }
                if (hwnd != IntPtr.Zero && hwnd != _dynamicExcludeLastHwnds[i])
                {
                    _dynamicExcludeLastHwnds[i] = hwnd;
                    NativeAddExclude(_handle, hwnd);
                }
            }
        }
    }

    private void SetCaptureEnabled(bool enabled)
    {
        if (_handle > 0) NativeSetCaptureEnabled(_handle, enabled ? (byte)1 : (byte)0);
    }

    public bool Initialize(IntPtr excludeHwnd)
    {
        if (_handle <= 0) return false;
        if (NativeInitialize(_handle, excludeHwnd, out byte ready, out int stage) != 0) return false;
        if (ready == 0 && stage > 0 && stage < InitFailureMessages.Length)
            RuntimeLog.Log(LogTag, InitFailureMessages[stage]);
        return ready != 0;
    }

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits, out int actualX, out int actualY)
    {
        actualX = x;
        actualY = y;
        if (_handle <= 0 || destBits == IntPtr.Zero || w <= 0 || h <= 0) return false;
        PollDynamicExcludeProviders();
        return NativeCaptureInto(_handle, x, y, w, h, destBits, out byte ok) == 0 && ok != 0;
    }

    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits) =>
        CaptureInto(x, y, w, h, destBits, out _, out _);

    // Recovery probes must not wait for a callback or interrupt the working backend.
    internal bool HasUsableFrame(int x, int y, int width, int height)
    {
        if (_handle <= 0 || width <= 0 || height <= 0) return false;
        PollDynamicExcludeProviders();
        return NativeHasUsableFrame(_handle, x, y, width, height, out byte usable) == 0 && usable != 0;
    }

    internal unsafe string GetDebugStatus(int x, int y, int width, int height)
    {
        if (_handle <= 0) return "native source unavailable";
        const int capacity = 512;
        char* buffer = stackalloc char[capacity];
        return NativeDebugStatus(_handle, x, y, width, height, buffer, capacity, out int length) == 0
            ? new string(buffer, 0, length)
            : "status unavailable";
    }

    /// <summary>Installs a completed frame without starting the pump thread (BGRA pixels, <paramref name="frameAgeMs"/> old).</summary>
    internal unsafe void InstallFrameForTest(bool ready, bool running, int width, int height, byte[] pixels, long frameAgeMs)
    {
        fixed (byte* bytes = pixels)
            NativeTestInstallFrame(_handle, ready ? (byte)1 : (byte)0, running ? (byte)1 : (byte)0, width, height, bytes, pixels.Length, frameAgeMs);
    }

    // Signals the pump thread to exit and waits briefly. Returns true when the thread is gone.
    private void StopPumpThread() =>
        _ = _handle <= 0 || (NativeStop(_handle, out byte exited) == 0 && exited != 0);


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_handle <= 0) return;
        if (NativeDestroy(_handle, out byte released) == 0 && released != 0) return;
        // The pump thread may still call into the callback. The native object is kept alive rather than crash.
        RuntimeLog.Log(LogTag, "Magnifier pump thread did not exit within 500ms; leaking the native source.");
    }
}
