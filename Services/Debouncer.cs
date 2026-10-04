using System.Windows.Threading;

namespace VNotch.Services;

public sealed class Debouncer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private Action? _pendingAction;
    private volatile bool _disposed;
    public Debouncer(TimeSpan delay, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(priority, _dispatcher)
        {
            Interval = delay
        };
        _timer.Tick += OnTick;
    }
    public void Debounce(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunOnDispatcher(() =>
        {
            if (_disposed) return;
            _pendingAction = action;
            _timer.Stop();
            _timer.Start();
        });
    }
    public void Cancel()
    {
        RunOnDispatcher(() =>
        {
            _timer.Stop();
            _pendingAction = null;
        });
    }
    public void Flush()
    {
        RunOnDispatcher(InvokePending);
    }

    private void OnTick(object? sender, EventArgs args) => InvokePending();

    private void InvokePending()
    {
        _timer.Stop();
        var action = _pendingAction;
        _pendingAction = null;
        if (!_disposed) action?.Invoke();
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RunOnDispatcher(() =>
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _pendingAction = null;
        });
    }
}

public sealed class Throttler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action? _pendingAction;
    private DateTime _lastExecutionUtc = DateTime.MinValue;
    private readonly TimeSpan _interval;

    public Throttler(TimeSpan interval, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        _interval = interval;
        _timer = new DispatcherTimer(priority)
        {
            Interval = interval
        };
        _timer.Tick += (s, e) =>
        {
            _timer.Stop();
            if (_pendingAction != null)
            {
                _lastExecutionUtc = DateTime.UtcNow;
                var action = _pendingAction;
                _pendingAction = null;
                action.Invoke();
            }
        };
    }
    public void Throttle(Action action)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastExecutionUtc) >= _interval)
        {
            _lastExecutionUtc = now;
            _timer.Stop();
            _pendingAction = null;
            action.Invoke();
        }
        else
        {
            _pendingAction = action;
            if (!_timer.IsEnabled)
                _timer.Start();
        }
    }
    public void Cancel()
    {
        _timer.Stop();
        _pendingAction = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _pendingAction = null;
    }
}
