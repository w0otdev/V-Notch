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
    private long _handle, _nextRetry;

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_registry_watch_create(nint hive, string path, int length, nint signal, out long handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_registry_watch_consume(long handle, out byte changed);
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
        long now = _tickCount?.Invoke() ?? Environment.TickCount64;
        if (_handle == 0 && now < _nextRetry) return false;
        if (_handle == 0)
        {
            bool hiveRef = false, eventRef = false;
            var hiveHandle = _hive.Handle;
            var eventHandle = _changed.SafeWaitHandle;
            try
            {
                hiveHandle.DangerousAddRef(ref hiveRef);
                eventHandle.DangerousAddRef(ref eventRef);
                vn_registry_watch_create(hiveHandle.DangerousGetHandle(), _path, _path.Length,
                    eventHandle.DangerousGetHandle(), out _handle);
            }
            finally
            {
                if (eventRef) eventHandle.DangerousRelease();
                if (hiveRef) hiveHandle.DangerousRelease();
            }
        }
        if (_handle != 0 && vn_registry_watch_consume(_handle, out byte changed) == 0) return changed != 0;
        if (_handle != 0) vn_registry_watch_close(_handle);
        _handle = 0;
        _nextRetry = now + 30_000;
        return true;
    }

    public void Dispose()
    {
        if (_handle != 0) vn_registry_watch_close(_handle);
        _handle = 0;
        _wait?.Unregister(null);
        _changed.Dispose();
    }
}
