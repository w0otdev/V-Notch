using System;
using System.Threading;

namespace VNotch.Services;

public sealed class SingleInstanceGuard : IDisposable
{
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
