using System;
using System.Windows.Threading;

namespace VNotch.Services;

public sealed class Debouncer : IDisposable
{
    private readonly UiScheduledTimer _timer;
    private readonly Dispatcher _dispatcher;
    private Action? _pendingAction;
    private volatile bool _disposed;
    public Debouncer(TimeSpan delay, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new UiScheduledTimer(priority, _dispatcher)
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
