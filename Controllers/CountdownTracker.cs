using System;
using System.Diagnostics;
using VNotch.ViewModels;

namespace VNotch.Controllers;

public sealed class CountdownTracker
{
    private readonly TimerViewModel _viewModel;
    private long _lastCountdownTimestamp;

    public CountdownTracker(TimerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public long LastCountdownTimestamp => _lastCountdownTimestamp;

    public void ResetCountdownTimestamp()
    {
        _lastCountdownTimestamp = Stopwatch.GetTimestamp();
    }

    public void ResetCountdownTimestamp(long timestamp)
    {
        _lastCountdownTimestamp = timestamp;
    }

    public bool AdvanceCountdown()
    {
        return AdvanceCountdown(Stopwatch.GetTimestamp());
    }

    public bool AdvanceCountdown(long now)
    {
        if (_lastCountdownTimestamp == 0)
        {
            _lastCountdownTimestamp = now;
            return false;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastCountdownTimestamp, now);
        _lastCountdownTimestamp = now;
        return _viewModel.Tick(elapsed);
    }

    public bool Pause()
    {
        return Pause(Stopwatch.GetTimestamp());
    }

    public bool Pause(long now)
    {
        bool completed = AdvanceCountdown(now);
        _viewModel.Pause();
        return completed;
    }

    public void Start()
    {
        Start(Stopwatch.GetTimestamp());
    }

    public void Start(long now)
    {
        _viewModel.Start();
        ResetCountdownTimestamp(now);
    }
}
