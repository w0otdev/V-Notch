using System;
using System.Windows;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{

    private IdleAutoHideController? _idleAutoHide;
    private bool _idlePointerInRevealZone;

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
            },
            isVisibilityFrozen: () => IsScreenCaptureActive);
        _notchManager.HoverService.MousePositionChanged += HoverService_MousePositionChangedForIdleAutoHide;
    }

    private void ApplyIdleAutoHideSettings()
    {
        _notchManager.HoverService.SetResponsivePolling(_settings.EnableIdleAutoHide);
        _idlePointerInRevealZone = _settings.EnableIdleAutoHide
            && Win32Interop.GetCursorPos(out var point)
            && IsPointInIdleRevealZone(new Point(point.X, point.Y));
        _idleAutoHide?.ApplySettings();
    }

    private bool IsPointInIdleRevealZone(Point point) =>
        _notchManager.HoverService.IsPointOverNotch(point)
        || _notchManager.HoverService.IsPointInTopEdgeRevealZone(point);

    private void HoverService_MousePositionChangedForIdleAutoHide(object? sender, Point point)
    {
        if (_cleanedUp || !_settings.EnableIdleAutoHide) return;
        if (IsScreenCaptureActive) return;

        bool wasInRevealZone = _idlePointerInRevealZone;
        // Use stable screen bounds: the hidden visual is collapsed or sliding off-screen.
        _idlePointerInRevealZone = IsPointInIdleRevealZone(point);
        if (_idlePointerInRevealZone)
            WakeFromIdle();
        else if (wasInRevealZone)
            _idleAutoHide?.Recheck();
    }

    private void WakeFromIdle() => _idleAutoHide?.Wake();

    internal void DisposeIdleAutoHide()
    {
        _notchManager.HoverService.MousePositionChanged -= HoverService_MousePositionChangedForIdleAutoHide;
        _idleAutoHide?.Dispose();
        _idleAutoHide = null;
    }

    private bool IsNotchEmptyAndIdle()
    {
        if (IsScreenCaptureActive) return false;
        if (IsStartupHoldActive(_isGreetingActive, _startupHoldUntilUtc, DateTime.UtcNow))
            return false;

        if (_isExpanded || _isMusicExpanded || _isTimerView || _isAnimating)
            return false;

        if (_isMusicCompactMode)
            return false;
        if (_currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying)
            return false;

        if (_compactPillArbiter.ActiveSlot != CompactPillSlot.None)
            return false;

        if (_isCountdownRunning || IsCountdownCompletionVisualActive || IsAppNotificationBannerActive)
            return false;

        if (_idlePointerInRevealZone || NotchWrapper.IsMouseOver)
            return false;
        if (_hwnd != IntPtr.Zero && IsCursorInsideNotchVisual())
            return false;

        return true;
    }
}
