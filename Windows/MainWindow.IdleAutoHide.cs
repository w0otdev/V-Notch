using System;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{

    private IdleAutoHideController? _idleAutoHide;

    private void InitializeIdleAutoHide() => InitializeIdleAutoHideController();

    internal void InitializeIdleAutoHideController()
    {
        _idleAutoHide = new IdleAutoHideController(
            _shellState,
            getSettings: () => _settings,
            isNotchEmptyAndIdle: IsNotchEmptyAndIdle,
            applyVisibilityState: ApplyNotchVisibilityState,
            stopHoverTimers: () =>
            {
                _hoverCollapseTimer.Stop();
                _hoverThumbnailDelayTimer.Stop();
            },
            onRevealed: () =>
            {
                TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
                EnsureTopmost(force: true);
            });
    }

    private void ApplyIdleAutoHideSettings() => _idleAutoHide?.ApplySettings();

    private void WakeFromIdle() => _idleAutoHide?.Wake();

    internal void DisposeIdleAutoHide()
    {
        _idleAutoHide?.Dispose();
        _idleAutoHide = null;
    }

    private bool IsNotchEmptyAndIdle()
    {
        if (_isExpanded || _isMusicExpanded || _isTimerView || _isAnimating)
            return false;

        if (_isMusicCompactMode)
            return false;
        if (_currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying)
            return false;

        if (_compactPillArbiter.ActiveSlot != CompactPillSlot.None)
            return false;

        if (_isCountdownRunning || IsCountdownCompletionVisualActive)
            return false;

        if (NotchWrapper.IsMouseOver)
            return false;
        if (_hwnd != IntPtr.Zero && IsCursorInsideWindow())
            return false;

        return true;
    }
}
