using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace VNotch.Services;

public sealed partial class SingleInstanceGuard : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_single_instance_guard_format_mutex_name", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeFormatMutexName(
        ReadOnlySpan<char> sid,
        int sidLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    internal static string GetCurrentUserMutexName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user SID is unavailable.");
        // Scope to this Windows session and user; same-user processes are still a trust boundary limitation.
        Span<char> buf = stackalloc char[256];
        if (NativeFormatMutexName(sid.AsSpan(), sid.Length, buf, buf.Length, out int len) == 0 && len > 0)
            return new string(buf.Slice(0, len));
        return $@"Local\VNotch_SingleInstance_{sid}";
    }

    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    public string MutexName { get; }

    public bool IsOwned => _ownsMutex;

    public SingleInstanceGuard(string mutexName)
    {
        MutexName = mutexName;
    }

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        return _ownsMutex;
    }

    public bool TryWaitForPreviousInstance(TimeSpan timeout)
    {
        if (_mutex == null)
            return false;

        try
        {
            if (_mutex.WaitOne(timeout))
            {
                _ownsMutex = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance terminated without releasing — we now own it.
            _ownsMutex = true;
            return true;
        }

        return false;
    }

    public void Release()
    {
        if (!_ownsMutex || _mutex == null)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            // Swallow: shutting down, don't crash on mutex release failure.
            System.Diagnostics.Debug.WriteLine($"SingleInstanceGuard: ReleaseMutex failed: {ex.Message}");
        }

        _ownsMutex = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Release();
        _mutex?.Dispose();
        _mutex = null;
        _disposed = true;
    }
}
