using VNotch.Contracts;

namespace VNotch.ApplicationServices;

/// <summary>Lifecycle runs on its owner's scheduler; no dispatcher or native DLL is required.</summary>
public abstract class ModuleLifecycle(IModuleScheduler scheduler, Action<string, string> log) : INotchModule
{
    private IModuleTimer? _timer;
    private bool _initialized;
    private bool _disposed;
    private int _ticking;
    public abstract string ModuleName { get; }
    public abstract TimeSpan? TickInterval { get; }
    protected virtual TimeSpan? InitialTickDelay => null;
    public bool IsRunning { get; private set; }

    public void Initialize()
    {
        if (_initialized || _disposed) return;
        try
        {
            OnInitialize();
            if (TickInterval is TimeSpan interval)
            {
                _timer = scheduler.CreateTimer();
                _timer.Interval = interval;
                _timer.Tick += TimerOnTick;
            }
            _initialized = true;
        }
        catch (Exception ex) { log($"MODULE-{ModuleName}", $"Initialize failed: {ex}"); }
    }

    public void Start()
    {
        if (_disposed || IsRunning) return;
        Initialize();
        if (!_initialized) return;
        IsRunning = true;
        try
        {
            OnStart();
            var delay = InitialTickDelay;
            if (IsRunning && delay == null) Tick();
            else if (_timer != null && delay is { } first) _timer.Interval = first;
            if (IsRunning) _timer?.Start();
        }
        catch (Exception ex)
        {
            Stop();
            log($"MODULE-{ModuleName}", $"Start failed: {ex}");
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _timer?.Stop(); OnStop(); }
        catch (Exception ex) { log($"MODULE-{ModuleName}", $"Stop failed: {ex}"); }
    }

    private void TimerOnTick(object? sender, EventArgs args)
    {
        if (_timer != null && TickInterval is { } interval) _timer.Interval = interval;
        Tick();
    }

    public void Tick()
    {
        if (!IsRunning || _disposed || Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try { OnTick(); }
        catch (Exception ex) { log($"MODULE-{ModuleName}", $"Tick failed: {ex}"); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            if (_timer != null) { _timer.Tick -= TimerOnTick; _timer.Dispose(); _timer = null; }
            OnDispose();
        }
        catch (Exception ex) { log($"MODULE-{ModuleName}", $"Dispose failed: {ex}"); }
        GC.SuppressFinalize(this);
    }

    protected virtual void OnInitialize() { }
    protected virtual void OnStart() { }
    protected virtual void OnStop() { }
    protected abstract void OnTick();
    protected virtual void OnDispose() { }
}
