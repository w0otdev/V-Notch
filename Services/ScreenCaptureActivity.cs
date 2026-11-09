using System.Runtime.InteropServices;
using System.Text;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

/// <summary>Tracks the capture overlay and the short input handoff when it closes.</summary>
internal sealed class ScreenCaptureActivity
{
    private const int InputHandoffMs = 1000;
    private readonly Func<IntPtr> _foreground;
    private readonly Func<IntPtr, bool> _isCaptureWindow;
    private readonly Func<bool> _captureKeyDown;
    private readonly Func<long> _ticks;
    private readonly Func<IntPtr> _pointerWindow;
    private long _protectedUntil;

    internal ScreenCaptureActivity(Func<IntPtr>? foreground = null, Func<IntPtr, bool>? isCaptureWindow = null,
        Func<bool>? captureKeyDown = null, Func<long>? ticks = null, Func<IntPtr>? pointerWindow = null)
    {
        _foreground = foreground ?? GetForegroundWindow;
        _isCaptureWindow = isCaptureWindow ?? IsCaptureWindow;
        _captureKeyDown = captureKeyDown ?? IsCaptureKeyDown;
        _ticks = ticks ?? (() => Environment.TickCount64);
        _pointerWindow = pointerWindow ?? WindowUnderPointer;
    }

    internal bool IsActive(IntPtr foreground = default)
    {
        long now = _ticks();
        if (_captureKeyDown() || _isCaptureWindow(foreground != IntPtr.Zero ? foreground : _foreground())
            || _isCaptureWindow(_pointerWindow()))
        {
            _protectedUntil = now + InputHandoffMs;
            return true;
        }
        return now < _protectedUntil;
    }

    private static readonly string[] CaptureProcessNames =
    [
        "snippingtool", "screenclippinghost", "screensketch", "sharex",
        "greenshot", "lightshot", "snagit32", "snagiteditor", "flameshot",
        "picpick", "screenpresso", "ksnip"
    ];

    internal static bool IsCaptureProcessName(string name)
    {
        foreach (string candidate in CaptureProcessNames)
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsCaptureOverlay(string className, string processName, bool borderlessFullscreen) =>
        className.Contains("ScreenClipping", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processName, "screenclippinghost", StringComparison.OrdinalIgnoreCase)
        || (borderlessFullscreen && IsCaptureProcessName(processName));

    internal static bool IsCaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var name = new StringBuilder(160);
        _ = GetClassName(hwnd, name, name.Capacity);
        string className = name.ToString();
        if (IsCaptureOverlay(className, "", false)) return true;
        string processName = FullscreenDetector.TryGetProcessName(hwnd);
        if (!IsCaptureProcessName(processName)) return false;
        if (IsCaptureOverlay(className, processName, false)) return true;
        // Capture utilities can also be normal editors or tray apps. Their mere
        // presence must not disable outside-click dismissal or fullscreen hiding.
        if ((GetWindowLong(hwnd, GWL_STYLE) & (WS_CAPTION | WS_THICKFRAME)) != 0
            || !GetWindowRect(hwnd, out var rect)) return false;
        var monitor = FullscreenDetector.GetWindowMonitor(hwnd);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        const int tolerance = 6;
        bool fullscreen = rect.Left <= info.rcMonitor.Left + tolerance && rect.Top <= info.rcMonitor.Top + tolerance
            && rect.Right >= info.rcMonitor.Right - tolerance && rect.Bottom >= info.rcMonitor.Bottom - tolerance;
        return IsCaptureOverlay(className, processName, fullscreen);
    }

    private static bool IsCaptureKeyDown() => (GetAsyncKeyState(0x2C) & 0x8000) != 0
        || ((GetAsyncKeyState(0x10) & 0x8000) != 0 && (GetAsyncKeyState(0x53) & 0x8000) != 0
            && ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0);

    private static IntPtr WindowUnderPointer() => GetCursorPos(out var point) ? WindowFromPoint(point) : IntPtr.Zero;
}
