using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VNotch.Services;

internal sealed partial class NativeWindowTitleMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle? _wait;
    private WatchHandle? _handle;
    private sealed class WatchHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => vn_media_title_watch_close(handle.ToInt64()) == 0;
    }
    private string? _lastSourceApp;
    private long _lastResolveTicks;
    private bool _hookActive;

    internal bool IsActive => Volatile.Read(ref _hookActive);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_title_watch_create(Microsoft.Win32.SafeHandles.SafeWaitHandle signal, out WatchHandle handle);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_title_watch_set_source(
        WatchHandle handle,
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        out byte outActive);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_title_watch_close(long handle);

    internal NativeWindowTitleMonitor(Action changed)
    {
        if (vn_media_title_watch_create(_signal.SafeWaitHandle, out _handle) == 0)
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, static (state, _) => ((Action)state!).Invoke(),
                changed, Timeout.Infinite, false);
    }

    internal bool UpdateSource(string? sourceApp, out bool changed)
    {
        lock (_sync)
        {
            changed = false;
            if (_handle == null || _handle.IsInvalid) return false;

            string normalized = sourceApp ?? string.Empty;
            long now = Environment.TickCount64;
            bool sourceChanged = !string.Equals(normalized, _lastSourceApp, StringComparison.Ordinal);
            if (!sourceChanged && (normalized.Length == 0 || now - _lastResolveTicks < 5000))
            {
                return _hookActive;
            }

            _lastSourceApp = normalized;
            _lastResolveTicks = now;
            bool active = vn_media_title_watch_set_source(_handle, normalized.AsSpan(), normalized.Length, out byte outActive) == 0 && outActive != 0;
            changed = sourceChanged || active != _hookActive;
            Volatile.Write(ref _hookActive, active);
            return active;
        }
    }

    public void Dispose()
    {
        WatchHandle? handle;
        lock (_sync)
        {
            handle = _handle;
            _handle = null;
            Volatile.Write(ref _hookActive, false);
        }
        handle?.Dispose();
        _wait?.Unregister(null);
        _signal.Dispose();
    }
}
