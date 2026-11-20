using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VNotch.Services;

// The native thread-pool wait rearms registry notifications before waking the worker.
internal sealed partial class RegistryChangeMonitor : IDisposable
{
    private readonly RegistryKey _hive;
    private readonly string _path;
    private readonly Func<long>? _tickCount;
    private readonly AutoResetEvent _changed = new(false);
    private readonly RegisteredWaitHandle? _wait;
    private readonly object _sync = new();
    private bool _disposed;
    private long _nextRetry;
    private WatchHandle? _handle;
    private sealed class WatchHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => vn_registry_watch_close(handle.ToInt64()) == 0;
    }

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_registry_watch_create(Microsoft.Win32.SafeHandles.SafeRegistryHandle hive, string path, int length,
        Microsoft.Win32.SafeHandles.SafeWaitHandle signal, out WatchHandle handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_registry_watch_consume(WatchHandle handle, out byte changed);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_registry_watch_close(long handle);

    internal RegistryChangeMonitor(RegistryKey hive, string path, Func<long>? tickCount = null, Action? onChanged = null)
    {
        _hive = hive; _path = path; _tickCount = tickCount;
        if (onChanged != null)
            _wait = ThreadPool.RegisterWaitForSingleObject(_changed, static (state, _) => ((Action)state!).Invoke(),
                onChanged, Timeout.Infinite, false);
    }

    internal bool ConsumeChange()
    {
        lock (_sync) return !_disposed && ConsumeChangeLocked();
    }

    private bool ConsumeChangeLocked()
    {
        long now = _tickCount?.Invoke() ?? Environment.TickCount64;
        if (_handle == null && now < _nextRetry) return false;
        if (_handle == null)
            vn_registry_watch_create(_hive.Handle, _path, _path.Length, _changed.SafeWaitHandle, out _handle);
        if (!_handle.IsInvalid && vn_registry_watch_consume(_handle, out byte changed) == 0) return changed != 0;
        _handle.Dispose();
        _handle = null;
        _nextRetry = now + 30_000;
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _handle?.Dispose();
            _handle = null;
        }
        _wait?.Unregister(null);
        _changed.Dispose();
    }
}
