using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

// Owned by one background worker. Failed subscriptions request a rescan, then back off.
internal sealed class RegistryChangeMonitor(RegistryKey hive, string path, Func<long>? tickCount = null) : IDisposable
{
    private readonly AutoResetEvent _changed = new(false);
    private RegistryKey? _key;
    private long _nextRetry;


    internal bool ConsumeChange()
    {
        long now = tickCount?.Invoke() ?? Environment.TickCount64;
        if (_key == null && now < _nextRetry) return false;
        try
        {
            if (_key != null && !_changed.WaitOne(0)) return false;
            if (_key == null)
                _key = hive.OpenSubKey(path, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.Notify);
            // Rearm before reading the registry so changes during a scan are not lost.
            if (_key != null)
            {
                bool keyRef = false;
                bool waitRef = false;
                try
                {
                    _key.Handle.DangerousAddRef(ref keyRef);
                    _changed.SafeWaitHandle.DangerousAddRef(ref waitRef);
#pragma warning disable S3869 // Handles are ref-counted via DangerousAddRef above
                    if (Win32Interop.RegistryNotifyChange(_key.Handle.DangerousGetHandle(), true,
                            0x10000000 | 0x1 | 0x4 | 0x8, _changed.SafeWaitHandle.DangerousGetHandle()))
#pragma warning restore S3869
                        return true;
                }
                finally
                {
                    if (waitRef) _changed.SafeWaitHandle.DangerousRelease();
                    if (keyRef) _key.Handle.DangerousRelease();
                }
            }
        }
        catch (Exception)
        {
            // Access changes and missing keys must not leave stale privacy evidence.
        }
        _key?.Dispose();
        _key = null;
        // The usage cache also reconciles missed changes every 30 seconds.
        // An inaccessible or absent hive must not force full scans every 1–2 seconds.
        _nextRetry = now + 30_000;
        return true;
    }

    public void Dispose()
    {
        _key?.Dispose();
        _changed.Dispose();
    }
}
