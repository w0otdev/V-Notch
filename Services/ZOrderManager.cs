using System.Windows.Threading;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

public sealed class ZOrderManager : IDisposable
{
    private readonly Func<IntPtr> _getHwnd;
    private readonly Func<bool> _isEffectivelyVisible;
    private readonly Func<bool> _isSuspended;
    private readonly Func<bool> _stayBehindWindows;
    private readonly Action<IntPtr> _onForegroundChanged;

    private readonly DispatcherTimer _watchdogTimer;
    private readonly DispatcherTimer _fastTimer;

    private IntPtr _foregroundWinEventHook = IntPtr.Zero;
    private WinEventDelegate? _foregroundWinEventProc;

    private DateTime _burstUntilUtc = DateTime.MinValue;
    private DateTime _fastUntilUtc = DateTime.MinValue;
    private DateTime _lastTopmostAssertUtc = DateTime.MinValue;

    private static readonly TimeSpan TopmostThrottle = TimeSpan.FromMilliseconds(80);
    public ZOrderManager(
        Func<IntPtr> getHwnd,
        Func<bool> isEffectivelyVisible,
        Func<bool> isSuspended,
        Func<bool> stayBehindWindows,
        Action<IntPtr> onForegroundChanged)
    {
        _getHwnd = getHwnd;
        _isEffectivelyVisible = isEffectivelyVisible;
        _isSuspended = isSuspended;
        _stayBehindWindows = stayBehindWindows;
        _onForegroundChanged = onForegroundChanged;

        _watchdogTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _watchdogTimer.Tick += WatchdogTimer_Tick;

        _fastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _fastTimer.Tick += FastTimer_Tick;
    }
    public void TriggerBurst(TimeSpan duration, bool aggressive = false)
    {
        var now = DateTime.UtcNow;
        var until = now + duration;

        if (until > _burstUntilUtc)
            _burstUntilUtc = until;

        if (aggressive)
        {
            var fastDuration = duration.TotalMilliseconds >= 800
                ? TimeSpan.FromMilliseconds(800)
                : duration;
            var fastUntil = now + fastDuration;

            if (fastUntil > _fastUntilUtc)
                _fastUntilUtc = fastUntil;

            if (!_fastTimer.IsEnabled)
                _fastTimer.Start();
        }
    }
    public void EnsureTopmost(bool force = false)
    {
        var hwnd = _getHwnd();
        if (hwnd == IntPtr.Zero || !_isEffectivelyVisible()) return;

        // Desktop mode deliberately opts out of the topmost watchdog. Keeping this
        // as a normal bottom-most window lets maximized applications own every
        // pixel (including browser tab controls) while the notch remains available
        // whenever the desktop is exposed.
        if (_stayBehindWindows())
        {
            SetWindowPos(hwnd, GetDesktopLayerInsertAfter(hwnd), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }

        if (_isSuspended()) return;

        var now = DateTime.UtcNow;
        if (!force && (now - _lastTopmostAssertUtc) < TopmostThrottle)
            return;

        if (force || Win32Interop.GetWindow(hwnd, GW_HWNDPREV) != IntPtr.Zero)
        {
            _lastTopmostAssertUtc = now;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    private void ForegroundWindowChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero || hwnd == _getHwnd())
            return;

        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
            new Action(() => _onForegroundChanged(hwnd)),
            DispatcherPriority.Send);
    }

    private void WatchdogTimer_Tick(object? sender, EventArgs e)
    {
        // Explorer may move its desktop host during Win+D, virtual-desktop, and
        // display transitions. Reassert desktop mode so the notch is visible as
        // soon as the desktop appears, without requiring an icon/file click.
        if (_stayBehindWindows())
        {
            EnsureTopmost(force: true);
            return;
        }
        if (_isSuspended()) return;

        var hwnd = _getHwnd();
        if (hwnd == IntPtr.Zero || !_isEffectivelyVisible()) return;

        var burstActive = DateTime.UtcNow <= _burstUntilUtc;
        var hasWindowAbove = Win32Interop.GetWindow(hwnd, GW_HWNDPREV) != IntPtr.Zero;

        if (burstActive || hasWindowAbove)
            EnsureTopmost(force: burstActive);
    }

    private void FastTimer_Tick(object? sender, EventArgs e)
    {
        if (_stayBehindWindows())
        {
            _fastTimer.Stop();
            return;
        }
        if (_isSuspended())
        {
            _fastTimer.Stop();
            return;
        }

        var hwnd = _getHwnd();
        if (hwnd == IntPtr.Zero || !_isEffectivelyVisible())
        {
            _fastTimer.Stop();
            return;
        }

        if (DateTime.UtcNow > _fastUntilUtc)
        {
            _fastTimer.Stop();
            return;
        }

        EnsureTopmost(force: true);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    public void Start()
    {
        if (_disposed) return;
        if (_foregroundWinEventHook == IntPtr.Zero)
        {
            _foregroundWinEventProc = ForegroundWindowChanged;
            _foregroundWinEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundWinEventProc,
                0, 0, WINEVENT_OUTOFCONTEXT);
        }
        _watchdogTimer.Start();
    }

    public void Stop()
    {
        _watchdogTimer.Stop();
        _fastTimer.Stop();

        if (_foregroundWinEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundWinEventHook);
            _foregroundWinEventHook = IntPtr.Zero;
        }
        _foregroundWinEventProc = null;
    }

    public void AssertNow()
    {
        EnsureTopmost(force: true);
    }
}
