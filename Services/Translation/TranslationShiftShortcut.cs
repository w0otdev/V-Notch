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
    internal TranslationShiftShortcut(Action requested)
    {
        _callback = (code, message, data) =>
        {
            if (!_disposed && code >= 0 && message.ToInt64() is 0x100 or 0x101 or 0x104 or 0x105)
            {
                var key = Marshal.PtrToStructure<Win32Interop.KBDLLHOOKSTRUCT>(data);
                bool otherModifier = IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
                if (_detector.Process(key.vkCode, message.ToInt64() is 0x100 or 0x104,
                    key.time, Win32Interop.GetForegroundWindow(), otherModifier || (key.flags & 0x12) != 0))
                    _dispatcher!.BeginInvoke(() => { if (!_disposed) requested(); });
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
            if (!_disposed) Dispatcher.Run();
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

internal sealed class ShiftTapDetector
{
    private uint _heldKey, _downTime, _lastTap;
    private bool _hasTap, _secondTap;
    private bool _waitingForQuiet;
    private uint _lastShiftEvent;
    private IntPtr _window;
    internal void Reset() { ResetTaps(); _waitingForQuiet = false; }
    private void ResetTaps() { _heldKey = 0; _hasTap = _secondTap = false; _window = IntPtr.Zero; }

    internal bool Process(uint key, bool down, uint time, IntPtr window, bool blocked = false)
    {
        bool shift = key is 0x10 or 0xA0 or 0xA1;
        if (shift)
        {
            // One shortcut per burst, even if focus changes while the popup opens.
            // Every repeat extends the quiet period; a fixed cooldown would keep
            // reopening the popup during a long stream of Shift taps.
            bool quiet = unchecked(time - _lastShiftEvent) > 500;
            _lastShiftEvent = time;
            if (_waitingForQuiet && !quiet) { ResetTaps(); return false; }
            if (quiet) _waitingForQuiet = false;
        }
        if (blocked || window == IntPtr.Zero || !shift) { ResetTaps(); return false; }
        if (_window != window) { ResetTaps(); _window = window; }
        if (down)
        {
            if (_heldKey == key) return false; // OS key repeat is not another tap.
            if (_heldKey != 0) { ResetTaps(); return false; } // simultaneous left/right Shift
            _secondTap = _hasTap && unchecked(time - _lastTap) <= 500;
            _heldKey = key; _downTime = time;
            return false;
        }
        if (_heldKey != key) { ResetTaps(); return false; }
        _heldKey = 0;
        if (unchecked(time - _downTime) > 300) { _hasTap = false; return false; }
        bool triggered = _secondTap;
        _hasTap = !triggered;
        _lastTap = time;
        if (triggered) _waitingForQuiet = true;
        return triggered;
    }
}
