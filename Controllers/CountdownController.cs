using System;
using System.Diagnostics;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controllers;

public sealed class CountdownCompletedEventArgs : EventArgs
{
    public long RunId { get; }

    public CountdownCompletedEventArgs(long runId)
    {
        RunId = runId;
    }
}

/// <summary>
/// Quản lý vòng đời chạy, tạm dừng, đo thời gian thực tế và thông báo hoàn tất của countdown.
/// Độc lập hoàn toàn với trạng thái hiển thị của WPF view (Workstream C - implement.md).
/// </summary>
public sealed class CountdownController : IDisposable
{
    private const string LogTag = "COUNTDOWN-CONTROLLER";
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimerViewModel _viewModel;
    private readonly CountdownTracker _tracker;
    private readonly DispatcherTimer _timer;
    private readonly Action<Action> _runOnUi;

    private long _activeRunId;
    private bool _hasFiredCompletedForRun;
    private bool _disposed;

    public event EventHandler<CountdownCompletedEventArgs>? Completed;
    public event EventHandler<long>? RunStarted;
    public event EventHandler<long>? RunPaused;
    public event EventHandler<long>? RunReset;
    public event EventHandler? Tick;

    public long ActiveRunId => _activeRunId;
    public bool IsRunning => _viewModel.IsRunning;
    public TimeSpan Duration => _viewModel.Duration;
    public TimeSpan Remaining => _viewModel.Remaining;
    public TimerViewModel ViewModel => _viewModel;
    public CountdownTracker Tracker => _tracker;

    public CountdownController(TimerViewModel viewModel, Action<Action> runOnUi)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _tracker = new CountdownTracker(_viewModel);
        _runOnUi = runOnUi ?? throw new ArgumentNullException(nameof(runOnUi));

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TickInterval
        };
        _timer.Tick += OnTimerTick;
    }

    public void Start()
    {
        if (_disposed) return;

        long runId = ++_activeRunId;
        _hasFiredCompletedForRun = false;
        _tracker.Start();
        _timer.Start();

        RuntimeLog.Log(LogTag, $"Countdown run #{runId} started ({_viewModel.Duration})");
        RunStarted?.Invoke(this, runId);
    }

    public void Pause()
    {
        if (_disposed) return;
        if (!_viewModel.IsRunning) return;

        _timer.Stop();
        bool completed = _tracker.Pause();

        long runId = _activeRunId;
        RuntimeLog.Log(LogTag, $"Countdown run #{runId} paused (Remaining: {_viewModel.Remaining})");
        RunPaused?.Invoke(this, runId);

        if (completed && !_hasFiredCompletedForRun)
        {
            _hasFiredCompletedForRun = true;
            TriggerCompleted(runId);
        }
    }

    public void Resume()
    {
        if (_disposed) return;
        if (_viewModel.IsRunning || _viewModel.Remaining <= TimeSpan.Zero) return;

        _tracker.Start();
        _timer.Start();

        RuntimeLog.Log(LogTag, $"Countdown run #{_activeRunId} resumed");
        RunStarted?.Invoke(this, _activeRunId);
    }

    public void Reset()
    {
        if (_disposed) return;

        _timer.Stop();
        long runId = ++_activeRunId;
        _hasFiredCompletedForRun = false;
        _viewModel.Reset();
        _tracker.ResetCountdownTimestamp();

        RuntimeLog.Log(LogTag, $"Countdown reset (Duration: {_viewModel.Duration})");
        RunReset?.Invoke(this, runId);
    }

    public bool AdjustDuration(int direction)
    {
        if (_disposed || _viewModel.IsRunning) return false;

        bool adjusted = _viewModel.Adjust(direction);
        if (adjusted)
        {
            _tracker.ResetCountdownTimestamp();
        }
        return adjusted;
    }

    public bool SetCustomDuration(TimeSpan duration)
    {
        if (_disposed) return false;

        if (_viewModel.IsRunning)
        {
            _timer.Stop();
        }

        bool result = _viewModel.SetCustomDuration(duration);
        _tracker.ResetCountdownTimestamp();
        return result;
    }

    public bool Advance(long? nowTimestamp = null)
    {
        if (_disposed || !_viewModel.IsRunning) return false;

        bool completed = nowTimestamp.HasValue
            ? _tracker.AdvanceCountdown(nowTimestamp.Value)
            : _tracker.AdvanceCountdown();

        _runOnUi(() => Tick?.Invoke(this, EventArgs.Empty));

        if (completed)
        {
            _timer.Stop();
            long runId = _activeRunId;

            if (!_hasFiredCompletedForRun)
            {
                _hasFiredCompletedForRun = true;
                TriggerCompleted(runId);
            }
        }

        return completed;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        Advance();
    }

    private void TriggerCompleted(long runId)
    {
        RuntimeLog.Log(LogTag, $"Countdown run #{runId} completed!");
        _runOnUi(() =>
        {
            Completed?.Invoke(this, new CountdownCompletedEventArgs(runId));
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
