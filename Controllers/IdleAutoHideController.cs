using System;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class IdleAutoHideController : IDisposable
{
    private const int IdleHidePollIntervalMs = 100;
    private const int HiddenPollIntervalMs = 500;

    private readonly NotchShellState _shellState;
    private readonly Func<NotchSettings> _getSettings;
    private readonly Func<bool> _isNotchEmptyAndIdle;
    private readonly Action _applyVisibilityState;
    private readonly Action _stopHoverTimers;
    private readonly Action _onRevealed;

    private readonly UiScheduledTimer _timer;
    private long? _idleSinceTick;
    private bool _disposed;

    public IdleAutoHideController(
        NotchShellState shellState,
        Func<NotchSettings> getSettings,
        Func<bool> isNotchEmptyAndIdle,
        Action applyVisibilityState,
        Action stopHoverTimers,
        Action onRevealed)
    {
        _shellState = shellState ?? throw new ArgumentNullException(nameof(shellState));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _isNotchEmptyAndIdle = isNotchEmptyAndIdle ?? throw new ArgumentNullException(nameof(isNotchEmptyAndIdle));
        _applyVisibilityState = applyVisibilityState ?? throw new ArgumentNullException(nameof(applyVisibilityState));
        _stopHoverTimers = stopHoverTimers ?? throw new ArgumentNullException(nameof(stopHoverTimers));
        _onRevealed = onRevealed ?? throw new ArgumentNullException(nameof(onRevealed));

        _timer = new UiScheduledTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(IdleHidePollIntervalMs)
        };
        _timer.Tick += IdleHideTimer_Tick;
    }

    public UiScheduledTimer Timer => _timer;

    private bool IsHiddenByIdle
    {
        get => _shellState.IsHiddenByIdle;
        set => _shellState.IsHiddenByIdle = value;
    }

    public void ApplySettings()
    {
        if (_disposed) return;
        var settings = _getSettings();

        if (settings.EnableIdleAutoHide)
        {
            _idleSinceTick = null;
            if (!_timer.IsEnabled)
                _timer.Start();
            Recheck();
        }
        else
        {
            _timer.Stop();
            _idleSinceTick = null;

            if (IsHiddenByIdle)
            {
                IsHiddenByIdle = false;
                _applyVisibilityState();
                if (_shellState.IsEffectivelyNotchVisible)
                    _onRevealed();
            }
        }
    }

    private void IdleHideTimer_Tick(object? sender, EventArgs e) => Recheck();

    public void Recheck() => EvaluateIdleState(Environment.TickCount64);

    internal void EvaluateIdleState(long nowTick)
    {
        if (_disposed) return;
        var settings = _getSettings();

        if (!settings.EnableIdleAutoHide)
        {
            _timer.Stop();
            _idleSinceTick = null;
            return;
        }

        bool idle = _isNotchEmptyAndIdle();

        if (!idle)
        {
            _idleSinceTick = null;
            if (IsHiddenByIdle)
                Wake();
            SetNextCheck(IdleHidePollIntervalMs);
            return;
        }

        if (IsHiddenByIdle)
        {
            SetNextCheck(HiddenPollIntervalMs);
            return;
        }

        _idleSinceTick ??= nowTick;
        long remainingMs = settings.IdleAutoHideDelay - (nowTick - _idleSinceTick.Value);
        if (remainingMs <= 0)
        {
            HideForIdle();
            SetNextCheck(HiddenPollIntervalMs);
        }
        else
        {
            // Honor the deadline even for delays shorter than a polling interval.
            SetNextCheck((int)Math.Min(IdleHidePollIntervalMs, remainingMs));
        }
    }

    private void SetNextCheck(int milliseconds)
    {
        var interval = TimeSpan.FromMilliseconds(milliseconds);
        if (_timer.Interval != interval)
            _timer.Interval = interval;
    }

    private void HideForIdle()
    {
        if (IsHiddenByIdle) return;

        IsHiddenByIdle = true;
        RuntimeLog.Log("IDLE-HIDE", $"Notch hidden after {_getSettings().IdleAutoHideDelay}ms idle");

        _stopHoverTimers();

        _applyVisibilityState();
    }

    public void Wake()
    {
        if (_disposed || !_getSettings().EnableIdleAutoHide) return;

        _idleSinceTick = null;
        SetNextCheck(IdleHidePollIntervalMs);

        if (!IsHiddenByIdle) return;

        IsHiddenByIdle = false;
        RuntimeLog.Log("IDLE-HIDE", "Notch revealed from idle (activity detected)");

        _applyVisibilityState();

        if (_shellState.IsEffectivelyNotchVisible)
            _onRevealed();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Tick -= IdleHideTimer_Tick;
        _timer.Dispose();
    }
}
