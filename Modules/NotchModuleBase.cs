using System;
using System.Windows.Threading;
using VNotch.Contracts;
using VNotch.Services;

namespace VNotch.Modules;

public abstract class NotchModuleBase : INotchModule
{
    private UiScheduledTimer? _timer;
    private bool _initialized;
    private bool _disposed;

    public abstract string ModuleName { get; }

    public abstract TimeSpan? TickInterval { get; }

    public bool IsRunning { get; private set; }

    public void Initialize()
    {
        if (_initialized || _disposed) return;

        try
        {
            OnInitialize();

            if (TickInterval is TimeSpan interval)
            {
                _timer = new UiScheduledTimer
                {
                    Interval = interval
                };
                _timer.Tick += TimerOnTick;
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Initialize failed: {ex}");
        }
    }

    public void Start()
    {
        if (_disposed) return;
        if (!_initialized) Initialize();
        if (IsRunning) return;

        IsRunning = true;
        try
        {
            OnStart();

            if (IsRunning)
                TickSafe();

            if (IsRunning)
                _timer?.Start();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            try { OnStop(); } catch { }
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Start failed: {ex}");
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        try
        {
            _timer?.Stop();
            OnStop();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Stop failed: {ex}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Tick()
    {
        if (!IsRunning || _disposed) return;
        TickSafe();
    }

    private void TimerOnTick(object? sender, EventArgs e) => TickSafe();

    private void TickSafe()
    {
        try
        {
            OnTick();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Tick failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            Stop();

            if (_timer != null)
            {
                _timer.Tick -= TimerOnTick;
                _timer = null;
            }

            OnDispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log($"MODULE-{ModuleName}", $"Dispose failed: {ex}");
        }
        finally
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    protected virtual void OnInitialize() { /* Optional lifecycle hook. */ }

    protected virtual void OnStart() { /* Optional lifecycle hook. */ }

    protected virtual void OnStop() { /* Optional lifecycle hook. */ }

    protected abstract void OnTick();

    protected virtual void OnDispose() { /* Optional lifecycle hook. */ }
}
