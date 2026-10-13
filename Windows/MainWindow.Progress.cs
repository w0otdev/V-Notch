using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;
using POINT = VNotch.Services.Win32Interop.POINT;
namespace VNotch;

public partial class MainWindow
{
    private DateTime _suppressOutsideClickUntilUtc = DateTime.MinValue;
    private DateTime _suppressHoverCollapseUntilUtc = DateTime.MinValue;
    private ProgressEngine _progressEngine => MediaPlayback.Progress;
    private double _progressDisplayRatio { get => MediaPlayback.DisplayRatio; set => MediaPlayback.DisplayRatio = value; }
    private int _lastDisplayedSecond { get => MediaPlayback.LastDisplayedSecond; set => MediaPlayback.LastDisplayedSecond = value; }
    private void StopSpringRenderLoop() => MediaPlayback.StopSpringRenderLoop();
    private void StopCatchUpAnimation() => MediaPlayback.StopCatchUpAnimation();
    private void StopRewindTextAnimation() => MediaPlayback.StopRewindTextAnimation();
    private void StartProgressCatchUpAnimation() => MediaPlayback.StartProgressCatchUpAnimation();
    private void RenderProgressBar() => MediaPlayback.RenderProgressBar();
    private void ResetProgressUI() => MediaPlayback.ResetProgressUI();
    private void UpdateProgressTracking(MediaInfo info) => MediaPlayback.UpdateProgressTracking(info);
    private void AnimateSeekProgressTo(double ratio) => MediaPlayback.AnimateSeekProgressTo(ratio);
    private Task SeekToPosition(TimeSpan position) => MediaPlayback.SeekToPosition(position);

    private void LyricsTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExpanded && !_isMusicExpanded && _isLyricsActive)
        {
            UpdateLyricsDisplay();
        }
    }

    private void VolumeSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExpanded && _isMusicExpanded && !_isDraggingVolume && DateTime.UtcNow >= _volumeScrollSyncAfterUtc &&
            _mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
        {
            _currentVolume = volume;
            SetVolumeFill(VolumeBarScale, _currentVolume);
            UpdateVolumeIcon(_currentVolume, isMuted);
        }
    }

    private readonly Queue<OutsideClickSnapshot> _outsideClicks = new();
    private Action? _drainOutsideClicks;
    private bool _outsideClickDrainPending;
    private readonly record struct OutsideClickSnapshot(InputMonitorService.POINT Point);

    private void GlobalMouseHook_MouseLeftButtonDown(object? sender, InputMonitorService.POINT pt)
    {
        if (IsGreetingInteractionBlocked) return;
        if (_cleanedUp || _spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility ||
            (!_isExpanded && !_isMusicExpanded)) return;
        // Capture against the visible shell at mouse-down, before queued layout
        // or animation work can move/resize it under this click.
        _outsideClicks.Enqueue(new OutsideClickSnapshot(pt));
        if (_outsideClickDrainPending) return;
        _outsideClickDrainPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, _drainOutsideClicks ??= DrainOutsideClicks);
    }

    private void DrainOutsideClicks()
    {
        try
        {
            while (_outsideClicks.TryDequeue(out var click))
            {
                if (_cleanedUp) { _outsideClicks.Clear(); break; }
                HandleOutsideClick(click);
            }
        }
        finally { _outsideClickDrainPending = false; }
    }

    private void HandleOutsideClick(OutsideClickSnapshot click)
    {
        var pt = click.Point;
        if (_clipboardHistory?.IsDragging == true || ClipboardTrayView.IsContextMenuOpen || ClipboardTrayView.ContainsMenuPoint(new Point(pt.x, pt.y))) return;
        // Clicks inside Spotlight window must not collapse MainWindow's hidden
        // state while Spotlight temporarily owns the notch surface.
        if (_spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;



        if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
        {
            IntPtr hWndAtPoint = WindowFromPoint(new POINT { X = pt.x, Y = pt.y });

            if (!IsScreenPointInsideNotchVisual(pt))
            {
                if (DateTime.UtcNow < _suppressOutsideClickUntilUtc)
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"Suppressed during thumbnail animation grace: pt=({pt.x},{pt.y}) " +
                        $"hWndAtPoint=0x{hWndAtPoint:X} remaining={((_suppressOutsideClickUntilUtc - DateTime.UtcNow).TotalMilliseconds):F0}ms");
                    return;
                }

                RuntimeLog.Log("COLLAPSE-TRIGGER",
                    $"Outside click detected: pt=({pt.x},{pt.y}) hWndAtPoint=0x{hWndAtPoint:X} _hwnd=0x{_hwnd:X} " +
                    $"isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded} isSecondary={_isSecondaryView} " +
                    $"isAnimating={_isAnimating}");

                CollapseAll();
            }
        }
    }



    private bool IsScreenPointInsideNotchVisual(InputMonitorService.POINT pt)
    {
        if (_hwnd == IntPtr.Zero || NotchWrapper == null || NotchWrapper.ActualWidth <= 0 || NotchWrapper.ActualHeight <= 0)
            return false;

        if (!GetWindowRect(_hwnd, out var windowRect))
            return false;

        try
        {
            var bounds = NotchWrapper.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, NotchWrapper.ActualWidth, NotchWrapper.ActualHeight));
            var dpi = VisualTreeHelper.GetDpi(this);
            double padX = 8 * dpi.DpiScaleX;
            double padY = 8 * dpi.DpiScaleY;
            double left = windowRect.Left + bounds.Left * dpi.DpiScaleX - padX;
            double top = windowRect.Top + bounds.Top * dpi.DpiScaleY - padY;
            double right = windowRect.Left + bounds.Right * dpi.DpiScaleX + padX;
            double bottom = windowRect.Top + bounds.Bottom * dpi.DpiScaleY + padY;

            return pt.x >= left && pt.x <= right &&
                   pt.y >= top && pt.y <= bottom;
        }
        catch
        {
            return false;
        }
    }

    private bool IsCursorInsideNotchVisual()
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (!GetCursorPos(out var cursorPt)) return false;
        return IsScreenPointInsideNotchVisual(new InputMonitorService.POINT { x = cursorPt.X, y = cursorPt.Y });
    }

    private bool IsCursorInsideWindow()
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (!GetCursorPos(out var cursorPt)) return false;
        if (!GetWindowRect(_hwnd, out var rc)) return false;
        return cursorPt.X >= rc.Left && cursorPt.X <= rc.Right &&
               cursorPt.Y >= rc.Top && cursorPt.Y <= rc.Bottom;
    }

    private void UpdateProgressTimerState()
    {
        bool isExpanded = _isExpanded || _isMusicExpanded;

        bool hasTimeline = _currentMediaInfo != null &&
            (_currentMediaInfo.HasTimeline ||
             _currentMediaInfo.IsIndeterminate ||
             _currentMediaInfo.Duration.TotalSeconds > 0 ||
             _currentMediaInfo.IsAnyMediaPlaying);
        bool shouldRunProgress = isExpanded && hasTimeline;
        bool shouldRunLyrics = _isExpanded && !_isMusicExpanded;
        bool shouldRunVolumeSync = _isExpanded && _isMusicExpanded;

        MediaPlayback.UpdateProgressTimerState();
        SetTimerEnabled(_lyricsTimer, shouldRunLyrics);
        SetTimerEnabled(_volumeSyncTimer, shouldRunVolumeSync);

        if (isExpanded)
        {
            InputMonitorService.Start(_hwnd);
        }
        else
        {
            InputMonitorService.Stop();
        }
    }

    private static void SetTimerEnabled(UiScheduledTimer timer, bool shouldRun)
    {
        if (shouldRun == timer.IsEnabled) return;

        if (shouldRun)
            timer.Start();
        else
            timer.Stop();
    }


}
