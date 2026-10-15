using System.Windows.Threading;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services;

public sealed class ZOrderManager : IDisposable
{
    private static IntPtr _currentForegroundWindow;
    private static int _activeForegroundHooks;

    internal static IntPtr GetForegroundWindowCached() => Volatile.Read(ref _activeForegroundHooks) > 0
        ? Interlocked.CompareExchange(ref _currentForegroundWindow, IntPtr.Zero, IntPtr.Zero)
        : GetForegroundWindow();
    private readonly Func<IntPtr> _getHwnd;
    private readonly Func<bool> _isEffectivelyVisible;
    private readonly Func<bool> _isSuspended;
    private readonly Func<bool> _stayBehindWindows;
    private readonly Action<IntPtr> _onForegroundChanged;

    private readonly Dispatcher _dispatcher;

    private IntPtr _foregroundWinEventHook = IntPtr.Zero;
    private WinEventDelegate? _foregroundWinEventProc;
    private IntPtr _pendingForegroundWindow;
    private int _foregroundDispatchQueued;

    private long _lastTopmostAssertTick = long.MinValue;
    private CancellationTokenSource? _burstCancellation;

    private const long TopmostThrottleMs = 80;
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
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void TriggerBurst(TimeSpan duration, bool aggressive = false)
    {
        if (_disposed || duration <= TimeSpan.Zero) return;

        EnsureTopmost(force: true);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _burstCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        ReassertAfterForegroundTransitionAsync(duration, aggressive, cancellation.Token)
            .SafeFireAndForget("ZORDER-BURST");
    }
    public void EnsureTopmost(bool force = false)
    {
        if (_disposed) return;
        var hwnd = _getHwnd();
        if (hwnd == IntPtr.Zero || !_isEffectivelyVisible()) return;

        // Desktop mode opts out of topmost watchdog so maximized apps own screen space
        // while notch remains accessible on desktop reveal.
        if (_stayBehindWindows())
        {
            if (!force && IsWindowDirectlyAboveDesktop(hwnd, out _))
                return;

            SetWindowPos(hwnd, GetDesktopLayerInsertAfter(hwnd), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }

        if (_isSuspended()) return;

        long nowTick = Environment.TickCount64;
        long lastTick = Interlocked.Read(ref _lastTopmostAssertTick);
        if (!force && lastTick != long.MinValue && (nowTick - lastTick) < TopmostThrottleMs)
            return;

        if (force || Win32Interop.GetWindow(hwnd, GW_HWNDPREV) != IntPtr.Zero)
        {
            Interlocked.Exchange(ref _lastTopmostAssertTick, nowTick);
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
        if (eventType == EVENT_SYSTEM_FOREGROUND)
            Interlocked.Exchange(ref _currentForegroundWindow, hwnd);
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero || hwnd == _getHwnd())
            return;

        var dispatcher = _dispatcher;
        if (_disposed || dispatcher.HasShutdownStarted) return;
        System.Threading.Interlocked.Exchange(ref _pendingForegroundWindow, hwnd);
        if (System.Threading.Interlocked.Exchange(ref _foregroundDispatchQueued, 1) != 0) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            System.Threading.Interlocked.Exchange(ref _foregroundDispatchQueued, 0);
            IntPtr pending = System.Threading.Interlocked.Exchange(ref _pendingForegroundWindow, IntPtr.Zero);
            if (!_disposed && pending != IntPtr.Zero) _onForegroundChanged(pending);
        }), DispatcherPriority.Background);
    }

    private async Task ReassertAfterForegroundTransitionAsync(
        TimeSpan duration,
        bool aggressive,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan firstDelay = aggressive && duration > TimeSpan.FromMilliseconds(250)
                ? TimeSpan.FromMilliseconds(250)
                : duration;
            await Task.Delay(firstDelay, cancellationToken).ConfigureAwait(false);
            await ReassertOnDispatcherAsync(cancellationToken).ConfigureAwait(false);

            TimeSpan remaining = duration - firstDelay;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                await ReassertOnDispatcherAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task ReassertOnDispatcherAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        return _dispatcher.InvokeAsync(
            () => EnsureTopmost(force: true),
            DispatcherPriority.Background,
            cancellationToken).Task;
    }

    private volatile bool _disposed;

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
            if (_foregroundWinEventHook != IntPtr.Zero)
            {
                Interlocked.Exchange(ref _currentForegroundWindow, GetForegroundWindow());
                Interlocked.Increment(ref _activeForegroundHooks);
            }
        }
        AssertNow();
    }

    public void Stop()
    {
        var cancellation = Interlocked.Exchange(ref _burstCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();

        if (_foregroundWinEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundWinEventHook);
            _foregroundWinEventHook = IntPtr.Zero;
            Interlocked.Decrement(ref _activeForegroundHooks);
        }
        _foregroundWinEventProc = null;
    }

    public void AssertNow()
    {
        EnsureTopmost(force: true);
    }
}
