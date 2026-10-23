using System.Runtime.InteropServices;
using System.Windows.Threading;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>Owns the low-level hook and its message pump independently of WPF's UI thread.</summary>
internal sealed class DedicatedKeyboardHook : IDisposable
{
    private readonly Func<KBDLLHOOKSTRUCT, int, bool> _handleKey;
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _hook;
    private int _disposed;
    internal int InstallationError { get; private set; }
    internal int ManagedThreadId => _thread.ManagedThreadId;
    internal bool IsInstalled => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _hook) != IntPtr.Zero;

    internal DedicatedKeyboardHook(Func<KBDLLHOOKSTRUCT, int, bool> handleKey)
    {
        _handleKey = handleKey;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Spotlight keyboard hook" };
        _thread.Start();
        // Startup only installs the native hook; it never calls the UI dispatcher.
        ready.Task.GetAwaiter().GetResult();
    }

    private void Run(TaskCompletionSource ready)
    {
        LowLevelKeyboardProc procedure = HookProc;
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, procedure, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) InstallationError = Marshal.GetLastWin32Error();
            ready.TrySetResult();
            if (_hook != IntPtr.Zero) Dispatcher.Run();
        }
        catch (Exception ex)
        {
            InstallationError = ex.HResult;
        }
        finally
        {
            ready.TrySetResult();
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            GC.KeepAlive(procedure);
        }
    }

    private IntPtr HookProc(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                if (_handleKey(Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data), message.ToInt32()))
                    return new IntPtr(1);
            }
            catch
            {
                // Exceptions must never unwind through user32 or delay the keyboard chain.
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // No synchronous UI call is made from the hook thread, so shutdown cannot
        // deadlock with activation/layout work on the UI dispatcher.
        var dispatcher = _dispatcher;
        if (dispatcher == null || Volatile.Read(ref _hook) == IntPtr.Zero || dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(() =>
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            dispatcher.InvokeShutdown();
        }, DispatcherPriority.Send);
    }
}
