using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VNotch.Services;

internal sealed partial class NativeWindowTitleMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle? _wait;
    private long _handle;
    private string? _lastSourceApp;
    private long _lastResolveTicks;
    private bool _hookActive;

    internal bool IsActive => Volatile.Read(ref _hookActive);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_title_watch_create(nint signal, out long handle);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_title_watch_set_source(
        long handle,
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        out byte outActive);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_title_watch_close(long handle);

    internal NativeWindowTitleMonitor(Action changed)
    {
        if (vn_media_title_watch_create(_signal.SafeWaitHandle.DangerousGetHandle(), out _handle) == 0)
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, static (state, _) => ((Action)state!).Invoke(),
                changed, Timeout.Infinite, false);
    }

    internal bool UpdateSource(string? sourceApp, out bool changed)
    {
        lock (_sync)
        {
            changed = false;
            if (_handle == 0) return false;

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
        long handle;
        lock (_sync)
        {
            handle = _handle;
            _handle = 0;
            Volatile.Write(ref _hookActive, false);
        }
        if (handle != 0) vn_media_title_watch_close(handle);
        _wait?.Unregister(null);
        _signal.Dispose();
    }
}
