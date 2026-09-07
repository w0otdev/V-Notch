using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace VNotch.Controllers;

public sealed class TimerManager : IDisposable
{
    private readonly Dictionary<string, ManagedTimer> _timers = new();
    private readonly Dispatcher _dispatcher;

    public TimerManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }
    public void Register(string name, TimeSpan interval, Action handler)
    {
        if (_timers.TryGetValue(name, out var existing))
        {
            existing.Timer.Stop();
            existing.Timer.Tick -= existing.Handler;
            existing.Timer.Interval = interval;
            var newHandler = new EventHandler((s, e) => handler());
            existing.Timer.Tick += newHandler;
            _timers[name] = new ManagedTimer(existing.Timer, newHandler);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = interval
        };
        var eventHandler = new EventHandler((s, e) => handler());
        timer.Tick += eventHandler;
        _timers[name] = new ManagedTimer(timer, eventHandler);
    }
    public void RegisterOneShot(string name, TimeSpan delay, Action handler)
    {
        Register(name, delay, () =>
        {
            Stop(name);
            handler();
        });
    }
    public void Start(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
            managed.Timer.Start();
    }
    public void Stop(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
            managed.Timer.Stop();
    }
    public void Restart(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
        {
            managed.Timer.Stop();
            managed.Timer.Start();
        }
    }
    public void Restart(string name, TimeSpan newInterval)
    {
        if (_timers.TryGetValue(name, out var managed))
        {
            managed.Timer.Stop();
            managed.Timer.Interval = newInterval;
            managed.Timer.Start();
        }
    }
    public bool IsRunning(string name)
    {
        return _timers.TryGetValue(name, out var managed) && managed.Timer.IsEnabled;
    }
    public void SetInterval(string name, TimeSpan interval)
    {
        if (_timers.TryGetValue(name, out var managed))
            managed.Timer.Interval = interval;
    }
    public void Unregister(string name)
    {
        if (_timers.TryGetValue(name, out var managed))
        {
            managed.Timer.Stop();
            managed.Timer.Tick -= managed.Handler;
            _timers.Remove(name);
        }
    }
    public void StopAll()
    {
        foreach (var managed in _timers.Values)
            managed.Timer.Stop();
    }

    public void Dispose()
    {
        foreach (var managed in _timers.Values)
        {
            managed.Timer.Stop();
            managed.Timer.Tick -= managed.Handler;
        }
        _timers.Clear();
    }

    private sealed record ManagedTimer(DispatcherTimer Timer, EventHandler Handler);
}
