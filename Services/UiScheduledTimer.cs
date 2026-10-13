using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// A dispatcher-bound schedule. All schedules on one dispatcher share one wake-up
/// timer, which sleeps until the next deadline and stops when no work is due.
/// </summary>
public sealed class UiScheduledTimer : IDisposable
{
    private readonly DispatcherScheduler _scheduler;
    private readonly DispatcherPriority _priority;
    private TimeSpan _interval;
    private bool _enabled;
    private bool _disposed;
    private long _version;
    internal long DueTimestamp { get; private set; }
    internal bool IsPending { get; private set; }

    public Dispatcher Dispatcher { get; }
    public event EventHandler? Tick;

    public UiScheduledTimer() : this(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) { }

    public UiScheduledTimer(DispatcherPriority priority) : this(priority, Dispatcher.CurrentDispatcher) { }

    public UiScheduledTimer(DispatcherPriority priority, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.VerifyAccess();
        if (priority is DispatcherPriority.Invalid or DispatcherPriority.Inactive)
            throw new ArgumentOutOfRangeException(nameof(priority));
        Dispatcher = dispatcher;
        _priority = priority;
        _scheduler = DispatcherScheduler.For(dispatcher);
    }

    public UiScheduledTimer(TimeSpan interval, DispatcherPriority priority, EventHandler callback, Dispatcher dispatcher)
        : this(priority, dispatcher)
    {
        Interval = interval;
        Tick += callback;
        Start();
    }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            Dispatcher.VerifyAccess();
            if (value < TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value));
            _interval = value;
            if (_enabled) ScheduleNext();
        }
    }

    public bool IsEnabled
    {
        get => _enabled;
        set { if (value) Start(); else Stop(); }
    }

    public void Start()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled || Dispatcher.HasShutdownStarted) return;
        _enabled = true;
        ScheduleNext();
    }

    public void Stop()
    {
        Dispatcher.VerifyAccess();
        _enabled = false;
        ++_version;
        IsPending = false;
        _scheduler.Remove(this);
    }

    private void ScheduleNext()
    {
        ++_version;
        IsPending = false;
        DueTimestamp = Stopwatch.GetTimestamp() +
            (long)(Math.Max(1, _interval.TotalSeconds * 1000) / 1000 * Stopwatch.Frequency);
        _scheduler.Add(this);
    }

    internal void DispatchTick()
    {
        long version = _version;
        IsPending = true;
        Dispatcher.BeginInvoke(_priority, new Action(() =>
        {
            if (!_enabled || _disposed || version != _version) return;
            try { Tick?.Invoke(this, EventArgs.Empty); }
            finally
            {
                // Stop, restart and interval changes inside a callback take precedence.
                if (_enabled && !_disposed && version == _version) ScheduleNext();
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        Tick = null;
    }

    private sealed class DispatcherScheduler
    {
        private static readonly ConditionalWeakTable<Dispatcher, DispatcherScheduler> Schedulers = new();
        private readonly DispatcherTimer _wakeUp;
        private readonly HashSet<UiScheduledTimer> _schedules = [];

        private DispatcherScheduler(Dispatcher dispatcher)
        {
            _wakeUp = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
            _wakeUp.Tick += OnWakeUp;
            dispatcher.ShutdownStarted += (_, _) =>
            {
                _wakeUp.Stop();
                _schedules.Clear();
            };
        }

        internal static DispatcherScheduler For(Dispatcher dispatcher) =>
            Schedulers.GetValue(dispatcher, static value => new DispatcherScheduler(value));

        internal void Add(UiScheduledTimer timer)
        {
            _schedules.Add(timer);
            ArmNextDeadline();
        }

        internal void Remove(UiScheduledTimer timer)
        {
            if (_schedules.Remove(timer)) ArmNextDeadline();
        }

        private void OnWakeUp(object? sender, EventArgs e)
        {
            _wakeUp.Stop();
            long now = Stopwatch.GetTimestamp();
            foreach (var schedule in _schedules.ToArray())
            {
                if (!schedule.IsPending && schedule.DueTimestamp <= now)
                    schedule.DispatchTick();
            }
            ArmNextDeadline();
        }

        private void ArmNextDeadline()
        {
            _wakeUp.Stop();
            long next = long.MaxValue;
            foreach (var schedule in _schedules)
                if (!schedule.IsPending) next = Math.Min(next, schedule.DueTimestamp);
            if (next == long.MaxValue || _wakeUp.Dispatcher.HasShutdownStarted) return;
            double milliseconds = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            _wakeUp.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(milliseconds), 1, int.MaxValue));
            _wakeUp.Start();
        }
    }
}
