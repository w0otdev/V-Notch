using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>Owns HWND hook, overlay styles, keyboard activation, and screen placement.</summary>
public sealed class OverlayWindowController : IDisposable
{
    private const double HorizontalPadding = 96;
    private readonly Window _window;
    private readonly NotchShellState _state;
    private readonly Func<bool> _isVisible;
    private readonly Func<bool> _stayBehindWindows;
    private readonly Action _ensureTopmost;
    private readonly Action _onAppDeactivated;
    private readonly Action _onDisplayChanged;
    private readonly Action _onClipboardUpdated;
    private HwndSource? _source;

    public OverlayWindowController(
        Window window,
        NotchShellState state,
        Func<bool> isVisible,
        Func<bool> stayBehindWindows,
        Action ensureTopmost,
        Action onAppDeactivated,
        Action onDisplayChanged,
        Action onClipboardUpdated)
    {
        _window = window;
        _state = state;
        _isVisible = isVisible;
        _stayBehindWindows = stayBehindWindows;
        _ensureTopmost = ensureTopmost;
        _onAppDeactivated = onAppDeactivated;
        _onDisplayChanged = onDisplayChanged;
        _onClipboardUpdated = onClipboardUpdated;
    }

    public void Initialize()
    {
        if (_source != null) return;
        _state.Hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_state.Hwnd);
        _source?.AddHook(WndProc);
    }

    public void ConfigureOverlay()
    {
        var exStyle = GetWindowLong(_state.Hwnd, GWL_EXSTYLE);
        var desiredStyle = exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;

        // Do not toggle WS_EX_TOPMOST through SetWindowLong. SetWindowPos below is
        // the documented way to move between topmost and non-topmost bands. Writing
        // the style first and then moving the HWND caused DWM to expose the layered
        // surface twice, producing a visible flash during desktop-edge reveal.
        if (desiredStyle != exStyle)
            SetWindowLong(_state.Hwnd, GWL_EXSTYLE, desiredStyle);

        _ensureTopmost();
    }

    public void SetKeyboardInput(bool enabled)
    {
        if (_state.Hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_state.Hwnd, GWL_EXSTYLE);
        SetWindowLong(_state.Hwnd, GWL_EXSTYLE,
            enabled ? exStyle & ~WS_EX_NOACTIVATE : exStyle | WS_EX_NOACTIVATE);
        if (enabled) _window.Activate();
    }

    public void PositionAtTop(double surfaceWidth, double expandedHeight)
    {
        double dpiScale = GetDpiScale();
        double widthDip = surfaceWidth + HorizontalPadding;
        double heightDip = expandedHeight + 80;

        int screenLeft = 0;
        int screenWidth = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpiScale);

        if (_state.Hwnd != IntPtr.Zero)
        {
            IntPtr hMonitor = MonitorFromWindow(_state.Hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    screenLeft = mi.rcMonitor.Left;
                    screenWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;
                }
            }
        }

        var bounds = CalculateCenteredBounds(
            screenLeft, screenWidth, widthDip, heightDip, dpiScale);

        _state.FixedX = bounds.X;
        _state.FixedY = 0;
        _state.WindowWidth = bounds.Width;
        _state.WindowHeight = bounds.Height;
        _state.HasFixedBounds = true;
        _window.Width = widthDip;
        _window.Height = heightDip;

        // Keep geometry independent from the desktop-layer anchor. Explorer rebuilds
        // its WorkerW/Progman windows during sign-in, so an expired anchor must never
        // be able to reject this placement and leave WPF's startup position visible.
        ApplyFixedBounds();
        ApplyPreferredZOrder();
    }

    public void ResizeHeight(double heightDip)
    {
        _window.Height = heightDip;
        _state.WindowHeight = (int)Math.Round(heightDip * GetDpiScale());
        ReassertBounds();
    }

    public void MoveFixedPosition(int newX, int newY)
    {
        _state.FixedX = newX;
        _state.FixedY = newY;
        ApplyFixedBounds();
    }

    public void ResetToCenteredTop(double widthDip, double heightDip)
    {
        PositionAtTop(Math.Max(600, widthDip - HorizontalPadding), Math.Max(200, heightDip - 80));
    }

    public void ReassertBounds()
    {
        if (!_state.HasFixedBounds || _state.WindowWidth <= 0 || _state.WindowHeight <= 0)
            return;

        ApplyFixedBounds();
        ApplyPreferredZOrder();
    }

    private bool ApplyFixedBounds()
    {
        if (_state.Hwnd == IntPtr.Zero)
            return false;

        bool positioned = SetWindowPos(
            _state.Hwnd,
            IntPtr.Zero,
            _state.FixedX,
            _state.FixedY,
            _state.WindowWidth,
            _state.WindowHeight,
            SWP_NOZORDER | SWP_NOACTIVATE);

        if (!positioned)
        {
            RuntimeLog.Warn("OVERLAY-POSITION",
                $"Failed to apply fixed bounds ({_state.FixedX},{_state.FixedY},{_state.WindowWidth}x{_state.WindowHeight}); Win32 error {Marshal.GetLastWin32Error()}");
        }

        return positioned;
    }

    private void ApplyPreferredZOrder()
    {
        if (_state.Hwnd == IntPtr.Zero)
            return;

        bool positioned = _stayBehindWindows()
            ? TryApplyDesktopLayerZOrder(
                () => GetDesktopLayerInsertAfter(_state.Hwnd),
                anchor => SetWindowPos(_state.Hwnd, anchor, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
            : SetWindowPos(_state.Hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        if (!positioned)
        {
            RuntimeLog.Warn("OVERLAY-ZORDER",
                $"Failed to apply the preferred z-order; Win32 error {Marshal.GetLastWin32Error()}");
        }
    }

    // Explorer may replace its desktop host between resolving the insertion point
    // and SetWindowPos. Resolve once more before giving up on a desktop-layer move.
    internal static bool TryApplyDesktopLayerZOrder(
        Func<IntPtr> getDesktopAnchor,
        Func<IntPtr, bool> applyZOrder)
    {
        if (applyZOrder(getDesktopAnchor()))
            return true;

        return applyZOrder(getDesktopAnchor());
    }

    private IntPtr PreferredZOrder => _stayBehindWindows()
        ? GetDesktopLayerInsertAfter(_state.Hwnd)
        : HWND_TOPMOST;

    public IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    public double DpiScale => GetDpiScale();

    public (double Left, double Top, double Width, double Height, double CornerRadius) GetNotchScreenRect(
        double notchWidth, double notchHeight, double cornerRadius)
    {
        double dpiScale = GetDpiScale();
        double windowLeft = _state.FixedX / dpiScale;
        double windowTop = _state.FixedY / dpiScale;
        double windowWidth = _state.WindowWidth / dpiScale;
        return (windowLeft + (windowWidth - notchWidth) / 2.0,
            windowTop, notchWidth, notchHeight, cornerRadius);
    }

    internal static (int X, int Width, int Height) CalculateCenteredBounds(
        int screenLeft, int screenWidth, double widthDip, double heightDip, double dpiScale)
    {
        int width = (int)Math.Round(widthDip * dpiScale);
        int height = (int)Math.Round(heightDip * dpiScale);
        return (screenLeft + (screenWidth - width) / 2, width, height);
    }

    private double GetDpiScale()
    {
        if (_state.Hwnd != IntPtr.Zero)
        {
            IntPtr hMonitor = MonitorFromWindow(_state.Hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out uint dpiY) == 0)
                {
                    if (dpiX > 0) return dpiX / 96.0;
                }
            }
        }

        if (_window != null)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_window);
            return dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        }

        return 1.0;
    }

    public Func<Point, bool>? IsPointInteractive { get; set; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsPointInteractive != null && _isVisible())
                {
                    try
                    {
                        short screenX = (short)(lParam.ToInt64() & 0xFFFF);
                        short screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                        double dpiScale = GetDpiScale();
                        if (dpiScale <= 0) dpiScale = 1.0;

                        Point windowPt;
                        try
                        {
                            windowPt = _window.PointFromScreen(new Point(screenX, screenY));
                        }
                        catch
                        {
                            double windowLeftDip = _state.FixedX / dpiScale;
                            double windowTopDip = _state.FixedY / dpiScale;
                            windowPt = new Point((screenX / dpiScale) - windowLeftDip, (screenY / dpiScale) - windowTopDip);
                        }

                        if (!IsPointInteractive(windowPt))
                        {
                            handled = true;
                            return new IntPtr(HTTRANSPARENT);
                        }
                    }
                    catch
                    {
                        handled = true;
                        return new IntPtr(HTTRANSPARENT);
                    }
                }
                break;
            case WM_WINDOWPOSCHANGING when lParam != IntPtr.Zero && _state.HasFixedBounds:
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);

                if ((pos.flags & SWP_NOMOVE) == 0)
                {
                    pos.y = _state.FixedY;
                    pos.x = _state.FixedX;
                }

                if ((pos.flags & SWP_NOSIZE) == 0 && _state.WindowWidth > 0 && _state.WindowHeight > 0)
                {
                    pos.cx = _state.WindowWidth;
                    pos.cy = _state.WindowHeight;
                }

                if ((pos.flags & SWP_NOZORDER) == 0)
                    pos.hwndInsertAfter = PreferredZOrder;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            case WM_ACTIVATE when _isVisible():
                SetWindowPos(_state.Hwnd, PreferredZOrder, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
            case WM_ACTIVATEAPP when wParam == IntPtr.Zero:
                _window.Dispatcher.BeginInvoke(_onAppDeactivated);
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                _window.Dispatcher.BeginInvoke(_onDisplayChanged);
                break;
            case WM_CLIPBOARDUPDATE:
                _onClipboardUpdated();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
