using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VNotch.Services.Translation;

// Shares the dedicated low-level keyboard hook loop: inference/UI stalls cannot time it out.
internal sealed class TranslationShiftShortcut : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly VNotch.Controllers.DedicatedKeyboardHook _hook;
    private readonly ShiftTapDetector _detector = new();

    private Dispatcher? _dispatcher;
    private volatile bool _disposed;

    internal TranslationShiftShortcut(Action requested, Func<IntPtr, bool>? isBlocked = null)
    {
        _hook = new VNotch.Controllers.DedicatedKeyboardHook((key, message) =>
        {
            if (!_disposed && message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                bool isShiftKey = key.vkCode is 0x10 or 0xA0 or 0xA1;
                if (!isShiftKey)
                {
                    if (message is WmKeyDown or WmSysKeyDown)
                        _detector.Reset();
                    return false;
                }

                bool otherModifier = IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
                var foreground = Win32Interop.GetForegroundWindow();
                if (_detector.Process(key.vkCode, message is WmKeyDown or WmSysKeyDown,
                    key.time, foreground, otherModifier || (key.flags & 0x12) != 0))
                {
                    _dispatcher?.BeginInvoke(() =>
                    {
                        // Process/fullscreen checks stay outside the low-level hook.
                        if (!_disposed && foreground == Win32Interop.GetForegroundWindow() && isBlocked?.Invoke(foreground) != true)
                            requested();
                    });
                }
            }
            // Never swallow: allow other listeners and the native hook chain to run.
            return false;
        });
        _dispatcher = _hook.Dispatcher;
        if (!_hook.IsInstalled)
        {
            RuntimeLog.Warn("TRANSLATION", "Could not register double-Shift shortcut.");
        }
    }

    private static bool IsDown(int key) => (Win32Interop.GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.Dispose();
        _detector.Reset();
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
