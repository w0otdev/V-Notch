using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

// Owned by one background worker. A failed/missing subscription always requests a rescan.
internal sealed class RegistryChangeMonitor(RegistryKey hive, string path) : IDisposable
{
    private readonly AutoResetEvent _changed = new(false);
    private RegistryKey? _key;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter, SafeWaitHandle signal,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    internal bool ConsumeChange()
    {
        try
        {
            if (_key != null && !_changed.WaitOne(0)) return false;
            if (_key == null)
                _key = hive.OpenSubKey(path, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.Notify);
            // Rearm before reading the registry so changes during a scan are not lost.
            if (_key != null && RegNotifyChangeKeyValue(_key.Handle, true,
                    0x10000000 | 0x1 | 0x4 | 0x8, _changed.SafeWaitHandle, true) == 0)
                return true;
        }
        catch (Exception)
        {
            // Access changes and missing keys must not leave stale privacy evidence.
        }
        _key?.Dispose();
        _key = null;
        return true;
    }

    public void Dispose()
    {
        _key?.Dispose();
        _changed.Dispose();
    }
}
