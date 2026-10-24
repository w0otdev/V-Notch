using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VNotch.Services.Translation;

// The hook has its own message loop: inference/UI stalls cannot time it out.
internal sealed class TranslationShiftShortcut : IDisposable
{
    private readonly Win32Interop.LowLevelKeyboardProc _callback;
    private readonly ShiftTapDetector _detector = new();
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _hook;
    private volatile bool _disposed;
    internal TranslationShiftShortcut(Action requested, Func<IntPtr, bool>? isBlocked = null)
    {
        _callback = (code, message, data) =>
        {
            if (!_disposed && code >= 0 && message.ToInt64() is 0x100 or 0x101 or 0x104 or 0x105)
            {
                var key = Marshal.PtrToStructure<Win32Interop.KBDLLHOOKSTRUCT>(data);
                bool otherModifier = IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
                var foreground = Win32Interop.GetForegroundWindow();
                if (_detector.Process(key.vkCode, message.ToInt64() is 0x100 or 0x104,
                    key.time, foreground, otherModifier || (key.flags & 0x12) != 0))
                    _dispatcher!.BeginInvoke(() =>
                    {
                        // Process/fullscreen checks stay outside the low-level hook.
                        if (!_disposed && foreground == Win32Interop.GetForegroundWindow() && isBlocked?.Invoke(foreground) != true) requested();
                    });
            }
            return Win32Interop.CallNextHookEx(_hook, code, message, data);
        };
        _thread = new Thread(Watch) { IsBackground = true, Name = "VNotch translation shortcut" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    private static bool IsDown(int key) => (Win32Interop.GetAsyncKeyState(key) & 0x8000) != 0;
    private void Watch()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Volatile.Write(ref _dispatcher, dispatcher);
        try
        {
            if (_disposed) return;
            _hook = Win32Interop.SetWindowsHookEx(13, _callback, Win32Interop.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) { RuntimeLog.Warn("TRANSLATION", "Could not register double-Shift shortcut."); return; }
            if (_disposed) return;
            dispatcher.BeginInvoke(() =>
            {
                if (_disposed && !dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }, DispatcherPriority.Send);
            Dispatcher.Run();
        }
        finally
        {
            if (_hook != IntPtr.Zero) Win32Interop.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _detector.Reset();
        }
    }
    public void Dispose()
    {
        _disposed = true;
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher != null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}

internal sealed partial class ShiftTapDetector
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnShiftTapState
    {
        public uint HeldKey;
        public uint DownTime;
        public uint LastTap;
        public uint LastShiftEvent;
        public uint LastOtherEvent;
        public IntPtr Window;
        public byte HasTap;
        public byte SecondTap;
        public byte WaitingForQuiet;
        public byte HasOtherEvent;
    }

    private VnShiftTapState _state;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transshift_reset(ref VnShiftTapState state);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_transshift_process(
        ref VnShiftTapState state,
        uint key, byte down, uint time, IntPtr window, byte blocked,
        out byte outTriggered);

    internal void Reset() => vn_transshift_reset(ref _state);

    internal bool Process(uint key, bool down, uint time, IntPtr window, bool blocked = false)
    {
        return vn_transshift_process(ref _state, key, down ? (byte)1 : (byte)0, time, window, blocked ? (byte)1 : (byte)0, out byte triggered) == 0 && triggered != 0;
    }
}
