using System.Runtime.InteropServices;
using System.Windows.Threading;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>Owns the low-level hook and its message pump independently of WPF's UI thread.</summary>
internal sealed class DedicatedKeyboardHook : IDisposable
{
    private readonly Thread _thread;
    private readonly LowLevelKeyboardHook _hook;
    private Dispatcher? _dispatcher;
    private int _disposed;
    internal int InstallationError { get; private set; }
    internal int ManagedThreadId => _thread.ManagedThreadId;
    internal bool IsInstalled => Volatile.Read(ref _disposed) == 0 && _hook.IsInstalled;

    internal DedicatedKeyboardHook(Func<KBDLLHOOKSTRUCT, int, bool> handleKey)
    {
        _hook = new LowLevelKeyboardHook((key, message) => Volatile.Read(ref _disposed) == 0 && handleKey(key, message));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Spotlight keyboard hook" };
        _thread.Start();
        // Startup only installs the native hook; it never calls the UI dispatcher.
        ready.Task.GetAwaiter().GetResult();
    }

    private void Run(TaskCompletionSource ready)
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            bool installed = _hook.Install();
            if (!installed) InstallationError = _hook.InstallationError;
            ready.TrySetResult();
            if (installed) Dispatcher.Run();
        }
        catch (Exception ex)
        {
            InstallationError = ex.HResult;
        }
        finally
        {
            ready.TrySetResult();
            _hook.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // No synchronous UI call is made from the hook thread, so shutdown cannot
        // deadlock with activation/layout work on the UI dispatcher.
        var dispatcher = _dispatcher;
        if (dispatcher == null || !_hook.IsInstalled || dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(() =>
        {
            _hook.Dispose();
            dispatcher.InvokeShutdown();
        }, DispatcherPriority.Send);
    }
}