using System;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class IdleAutoHideController : IDisposable
{
    private const int IdleHidePollIntervalMs = 500;

    private readonly NotchShellState _shellState;
    private readonly Func<NotchSettings> _getSettings;
    private readonly Func<bool> _isNotchEmptyAndIdle;
    private readonly Action _applyVisibilityState;
    private readonly Action _stopHoverTimers;
    private readonly Action _onRevealed;

    private readonly UiScheduledTimer _timer;
    private long _idleSinceTick;
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

        _timer = new UiScheduledTimer(DispatcherPriority.Background)
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
        var settings = _getSettings();

        if (settings.EnableIdleAutoHide)
        {
            _idleSinceTick = 0;
            if (!_timer.IsEnabled)
                _timer.Start();
        }
        else
        {
            _timer.Stop();
            _idleSinceTick = 0;

            if (IsHiddenByIdle)
            {
                IsHiddenByIdle = false;
                _applyVisibilityState();
                if (_shellState.IsEffectivelyNotchVisible)
                    _onRevealed();
            }
        }
    }

    private void IdleHideTimer_Tick(object? sender, EventArgs e)
    {
        var settings = _getSettings();

        if (!settings.EnableIdleAutoHide)
        {
            _timer.Stop();
            return;
        }

        bool idle = _isNotchEmptyAndIdle();

        if (!idle)
        {
            _idleSinceTick = 0;
            if (IsHiddenByIdle)
                Wake();
            return;
        }

        if (IsHiddenByIdle) return;

        long nowTick = Environment.TickCount64;
        if (_idleSinceTick == 0)
        {
            _idleSinceTick = nowTick;
            return;
        }

        if ((nowTick - _idleSinceTick) >= settings.IdleAutoHideDelay)
        {
            HideForIdle();
        }
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
        if (!_getSettings().EnableIdleAutoHide) return;

        _idleSinceTick = 0;

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

        _timer.Stop();
        _timer.Tick -= IdleHideTimer_Tick;
    }
}
