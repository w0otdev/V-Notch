using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class NativeWindowTitleMonitor : IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle? _wait;
    private long _handle;
    internal bool IsActive => _handle != 0;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_title_watch_create(nint signal, out long handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_title_watch_close(long handle);

    internal NativeWindowTitleMonitor(Action changed)
    {
        if (vn_media_title_watch_create(_signal.SafeWaitHandle.DangerousGetHandle(), out _handle) == 0)
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, static (state, _) => ((Action)state!).Invoke(),
                changed, Timeout.Infinite, false);
    }
    public void Dispose()
    {
        if (_handle != 0) vn_media_title_watch_close(_handle);
        _handle = 0;
        _wait?.Unregister(null);
        _signal.Dispose();
    }
}
