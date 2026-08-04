using System;
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
    private bool _isDraggingProgress = false;
    private bool _isProgressBarExpanded = false;
    private int _lastDisplayedSecond = -1;
    private TimeSpan _dragSeekPosition = TimeSpan.Zero;

    private readonly ProgressEngine _progressEngine = new ProgressEngine();
    private long _progressSnapshotSequence = 0;

    private double _progressDisplayRatio = 0;
    private double _progressTargetRatio = 0;
    private double _progressSpringTargetRatio = 0;
    private double _progressVelocity = 0;
    private bool _isSeekSpringActive = false;
    private int _springSettleFrames = 0;
    private DateTime _seekSpringStartTime = DateTime.MinValue;
    private bool _isClickSeekPending = false;
    private DateTime _allowProgressBackwardRenderUntil = DateTime.MinValue;
    private DateTime _blockBackwardAfterSeekUntil = DateTime.MinValue;
    private Point _mouseDownPoint;
    private const double DRAG_THRESHOLD = 3.0;
    private DateTime _suppressExternalSeekDetectionUntil = DateTime.MinValue;
    private DateTime _protectSpringTargetUntil = DateTime.MinValue;
    private DateTime _suppressOutsideClickUntilUtc = DateTime.MinValue;
    private DateTime _suppressHoverCollapseUntilUtc = DateTime.MinValue;

    private ProgressSpringRenderer? _springRenderer;
    private ProgressSpringRenderer Spring => _springRenderer ??= new ProgressSpringRenderer(
        applyRatio: r =>
        {
            _progressDisplayRatio = r;
            ProgressBarScale.ScaleX = r;
        },
        shouldRender: () => !_isDraggingProgress,
        getPlaybackRate: () => _currentMediaInfo?.PlaybackRate ?? 1.0);

    private const double NORMAL_LERP_SPEED = 14.0;
    private const double SOURCE_IGNORE_SECONDS = 0.30;
    private const double SOURCE_SMOOTH_SECONDS = 2.0;

    private static readonly TimeSpan EndSeekSafetyMargin = TimeSpan.FromMilliseconds(1500);

    private static TimeSpan ClampSeekTarget(TimeSpan target, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return target < TimeSpan.Zero ? TimeSpan.Zero : target;

        TimeSpan maxSeek = duration - EndSeekSafetyMargin;
        if (maxSeek < TimeSpan.Zero) maxSeek = TimeSpan.Zero;

        if (target > maxSeek) target = maxSeek;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        return target;
    }

    private void StartSpringRenderLoop()
    {
        Spring.DisplayRatio = _progressDisplayRatio;
        Spring.TargetRatio = _progressTargetRatio;
        Spring.SpringTargetRatio = _progressSpringTargetRatio;
        Spring.Velocity = _progressVelocity;
        Spring.SettleFrames = _springSettleFrames;
        Spring.Start();
    }

    private void StopSpringRenderLoop()
    {
        if (_springRenderer == null) return;
        Spring.Stop();
        _progressDisplayRatio = Spring.DisplayRatio;
        _progressVelocity = Spring.Velocity;
        _springSettleFrames = Spring.SettleFrames;
        _isSeekSpringActive = Spring.IsActive;
    }

    private void SyncSpringStateFromFields()
    {
        if (_springRenderer == null) return;
        Spring.DisplayRatio = _progressDisplayRatio;
        Spring.TargetRatio = _progressTargetRatio;
        Spring.SpringTargetRatio = _progressSpringTargetRatio;
        Spring.Velocity = _progressVelocity;
        Spring.SettleFrames = _springSettleFrames;
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        if ((_isExpanded || _isMusicExpanded) && _currentMediaInfo != null)
        {
            RenderProgressBar();
        }
    }

    private void LyricsTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExpanded && !_isMusicExpanded && _isLyricsActive)
        {
            UpdateLyricsDisplay();
        }
    }

    private void VolumeSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (_isExpanded && _isMusicExpanded && !_isDraggingVolume &&
            _mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
        {
            _currentVolume = volume;
            VolumeBarScale.ScaleX = _currentVolume;
            UpdateVolumeIcon(_currentVolume, isMuted);
        }
    }

    private DateTime _lastOutsideClickTime = DateTime.MinValue;

    private void GlobalMouseHook_MouseLeftButtonDown(object? sender, InputMonitorService.POINT pt)
    {
        Dispatcher.Invoke(() =>
        {
            // Spotlight temporarily owns the notch surface. Clicks inside its
            // window are outside MainWindow by definition and must not collapse
            // the hidden source state underneath the morph.
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

                    if (_isSecondaryView)
                    {
                        var now = DateTime.Now;
                        double doubleClickTime = GetDoubleClickTime();

                        if ((now - _lastOutsideClickTime).TotalMilliseconds < doubleClickTime)
                        {
                            RuntimeLog.Log("COLLAPSE-TRIGGER", "Secondary view double-click -> CollapseAll");
                            CollapseAll();
                            _lastOutsideClickTime = DateTime.MinValue;
                        }
                        else
                        {
                            _lastOutsideClickTime = now;
                        }
                    }
                    else
                    {
                        RuntimeLog.Log("COLLAPSE-TRIGGER", "Normal view single outside click -> CollapseAll");
                        CollapseAll();
                    }
                }
            }
        });
    }

    private bool IsChildWindow(IntPtr parent, IntPtr child)
    {
        IntPtr current = child;
        while (current != IntPtr.Zero)
        {
            if (current == parent) return true;
            current = GetParent(current);
        }
        return false;
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
            return IsCursorInsideWindow();
        }
    }

    private bool IsCursorInsideWindow()
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (!GetCursorPos(out var cursorPt)) return false;
        if (!GetWindowRect(_hwnd, out var rc)) return false;
        return cursorPt.X >= rc.Left && cursorPt.X <= rc.Right &&
               cursorPt.Y >= rc.Top && cursorPt.Y <= rc.Bottom;
    }

    private string _lastSessionId = "";
    private string _lastProgressSessionInstanceKey = "";
    private string _lastProgressSignature = "";
    private string _lastProgressTimelineKey = "";
    private DateTimeOffset _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
    private long _trackChangeSequence = 0;

    private void UpdateProgressTracking(MediaInfo info)
    {
        if (!info.IsThumbnailOnlyUpdate)
            _currentMediaInfo = info;
        NormalizeStartupSnapshotTimestamp(info);

        if (info.IsThumbnailOnlyUpdate)
        {
            UpdateProgressTimerState();
            if (_isExpanded || _isMusicExpanded) RenderProgressBar();
            return;
        }

        ProgressSection.Visibility = Visibility.Visible;
        ProgressSection.Opacity = 1;

        bool showProgressDetails = info.IsAnyMediaPlaying || info.Duration.TotalSeconds > 0;

        if (showProgressDetails || info.HasTimeline || info.IsIndeterminate)
        {
            if (_isDraggingProgress) return;

            string newSignature = $"{info.SourceAppId}|{info.CurrentTrack}|{info.CurrentArtist}";
            bool isTrackChanged = newSignature != _lastProgressSignature;
            bool isSessionSwitch = MediaProgressHelpers.HasLogicalSessionChanged(
                _lastSessionId,
                _lastProgressSessionInstanceKey,
                info.SourceAppId,
                info.SessionInstanceKey);
            bool isFirstEverTrack = string.IsNullOrEmpty(_lastProgressSignature);

            if (isTrackChanged && !isFirstEverTrack && !isSessionSwitch && info.IsPlaying && info.Position > TimeSpan.Zero)
            {
                string trackPrefix = $"{info.SourceAppId}|{info.CurrentTrack}|";
                if (_lastProgressSignature.StartsWith(trackPrefix, StringComparison.Ordinal))
                {
                    string oldArtist = _lastProgressSignature.Substring(trackPrefix.Length);
                    _lastProgressSignature = newSignature;
                    isTrackChanged = false;
                    RuntimeLog.Debug("PROGRESS-TRACK", () =>
                        $"Artist-only stabilization, skipping reset: '{oldArtist}' → '{info.CurrentArtist}' pos={info.Position.TotalSeconds:F1}s");
                }
            }

            if (isTrackChanged)
            {
                _lastProgressSignature = newSignature;
                double visibleRatio = Math.Clamp(ProgressBarScale.ScaleX, 0, 1);

                _trackChangeSequence++;

                _progressEngine.Reset();
                StopCatchUpAnimation();
                StopRewindTextAnimation();

                _isRewindAnimating = false;
                ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = false;
                _lastRenderTime = DateTime.MinValue;
                _lastRenderedDuration = TimeSpan.Zero;
                _lastDisplayedSecond = -1;
                _progressSnapshotSequence = 0;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                StopSpringRenderLoop();

                double fromRatio = visibleRatio;
                _progressDisplayRatio = fromRatio;
                _progressTargetRatio = 0;
                _progressSpringTargetRatio = 0;
                _lastRenderedRatio = 0;
                CurrentTimeText.Text = "0:00";
                RemainingTimeText.Text = FormatDuration(info.Duration);

                if (isFirstEverTrack)
                {
                    if (info.IsPlaying && info.Position > TimeSpan.Zero && info.Duration.TotalSeconds > 0)
                    {
                        double initialRatio = Math.Clamp(info.Position.TotalSeconds / info.Duration.TotalSeconds, 0, 1);
                        _progressDisplayRatio = initialRatio;
                        _progressTargetRatio = initialRatio;
                        _progressSpringTargetRatio = initialRatio;
                        _lastRenderedRatio = initialRatio;
                        ProgressBarScale.ScaleX = initialRatio;
                        CurrentTimeText.Text = FormatTime(info.Position);
                        RemainingTimeText.Text = FormatDuration(info.Duration);
                        RuntimeLog.Debug("PROGRESS-BOOT", () =>
                            $"First track init at pos={info.Position.TotalSeconds:F1}s ratio={initialRatio:F4}");
                    }
                    else
                    {
                        _progressDisplayRatio = 0;
                        ProgressBarScale.ScaleX = 0;
                    }
                }
                else if (_isExpanded || _isMusicExpanded)
                {
                    AnimateTrackChangeRewindToZero(fromRatio, info.Duration);
                }
                else
                {
                    _progressDisplayRatio = 0;
                    ProgressBarScale.ScaleX = 0;
                }

                _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(2);
            }
            else if (isSessionSwitch)
            {
                _progressEngine.Reset();
                _progressSnapshotSequence = 0;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
                _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
                RuntimeLog.Debug("PROGRESS-SESSION", () =>
                    $"Logical session changed for same track: " +
                    $"'{_lastProgressSessionInstanceKey}' -> '{info.SessionInstanceKey}'");
            }

            _lastSessionId = info.SourceAppId ?? "";
            _lastProgressSessionInstanceKey = info.SessionInstanceKey ?? "";

            string timelineKey = $"{info.SourceAppId}|{info.SessionInstanceKey}|{info.CurrentTrack}|{info.CurrentArtist}|{Math.Round(info.Duration.TotalSeconds):F0}";
            if (timelineKey != _lastProgressTimelineKey)
            {
                _lastProgressTimelineKey = timelineKey;
                _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
            }

            if (_lastProgressTimelineUpdated != DateTimeOffset.MinValue &&
                timelineKey == _lastProgressTimelineKey)
            {

                var infoUpdatedUtc = info.LastUpdated.ToUniversalTime();
                var lastUpdatedUtc = _lastProgressTimelineUpdated.ToUniversalTime();

                if (infoUpdatedUtc < lastUpdatedUtc)
                {
                    UpdateProgressTimerState();
                    if (_isExpanded || _isMusicExpanded) RenderProgressBar();
                    return;
                }
            }

            var snapshot = new ProgressSnapshot
            {
                Position = info.Position,
                Duration = info.Duration,
                IsPlaying = info.IsPlaying,

                IsYouTube = IsLikelyBrowserProgressSource(info),
                PlaybackRate = info.PlaybackRate,
                IsSeekEnabled = info.IsSeekEnabled,
                IsIndeterminate = info.IsIndeterminate,
                Timestamp = info.LastUpdated.UtcDateTime,
                SequenceNumber = System.Threading.Interlocked.Increment(ref _progressSnapshotSequence)
            };

            _progressEngine.OnMediaSnapshot(snapshot);
            if (info.LastUpdated > _lastProgressTimelineUpdated)
            {
                _lastProgressTimelineUpdated = info.LastUpdated;
            }

            bool isLiveStream = info.Duration.TotalSeconds <= 0 && info.IsPlaying;

            if (isLiveStream || info.IsIndeterminate)
            {
                bool wasIndeterminate = IndeterminateProgress.Visibility == Visibility.Visible;
                IndeterminateProgress.Visibility = Visibility.Visible;
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBarBg.Visibility = Visibility.Visible;
                if (!wasIndeterminate)
                {
                    StartIndeterminateAnimation();
                }
            }
            else
            {
                if (IndeterminateProgress.Visibility == Visibility.Visible)
                {
                    IndeterminateProgress.BeginAnimation(OpacityProperty, null);
                }
                IndeterminateProgress.Visibility = Visibility.Collapsed;
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBarBg.Visibility = Visibility.Visible;
            }

            bool canSeek = info.IsSeekEnabled && info.Duration.TotalSeconds > 0;
            ProgressBarContainer.Cursor = canSeek ? Cursors.Hand : Cursors.Arrow;

            UpdateProgressTimerState();

            if (_isExpanded || _isMusicExpanded)
            {
                RenderProgressBar();
                Dispatcher.BeginInvoke(new Action(RenderProgressBar), DispatcherPriority.Render);
            }
        }
        else
        {
            UpdateProgressTimerState();
            _lastSessionId = "";
            _lastProgressSessionInstanceKey = "";

            IndeterminateProgress.Visibility = Visibility.Collapsed;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBarContainer.Cursor = Cursors.Arrow;

            _progressEngine.Reset();
            _lastProgressTimelineKey = "";
            _lastProgressTimelineUpdated = DateTimeOffset.MinValue;
            _lastRenderedDuration = TimeSpan.Zero;
            ResetProgressUI();
            if (_isExpanded || _isMusicExpanded) RenderProgressBar();
        }
    }

    private static void NormalizeStartupSnapshotTimestamp(MediaInfo info) =>
        MediaProgressHelpers.NormalizeStartupSnapshotTimestamp(info);

    private static bool IsLikelyBrowserProgressSource(MediaInfo info) =>
        MediaProgressHelpers.IsLikelyBrowserProgressSource(info);

    private void StartIndeterminateAnimation()
    {
        if (IndeterminateProgress.Visibility != Visibility.Visible) return;

        var anim = new DoubleAnimation(0.3, 0.8, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        IndeterminateProgress.BeginAnimation(OpacityProperty, anim);
    }

    private void ResetProgressUI()
    {
        _trackChangeSequence++;
        _isRewindAnimating = false;
        ProgressBarContainer.BeginAnimation(OpacityProperty, null);
        ProgressBarContainer.Opacity = 1;
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ProgressBarMainScale.ScaleY = 1;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = 0;
        _progressDisplayRatio = 0;
        _progressTargetRatio = 0;
        _progressSpringTargetRatio = 0;
        _progressVelocity = 0;
        _springSettleFrames = 0;
        _isSeekSpringActive = false;
        _seekSpringStartTime = DateTime.MinValue;
        _blockBackwardAfterSeekUntil = DateTime.MinValue;
        _lastRenderTime = DateTime.MinValue;
        _lastRenderedDuration = TimeSpan.Zero;
        _lastDisplayedSecond = -1;
        CurrentTimeText.Text = "0:00";
        RemainingTimeText.Text = "0:00";
        IndeterminateProgress.BeginAnimation(OpacityProperty, null);
        IndeterminateProgress.Visibility = Visibility.Collapsed;
    }

    private DateTime _lastRenderTime = DateTime.MinValue;
    private TimeSpan _lastRenderedDuration = TimeSpan.Zero;
    private double _lastRenderedRatio = 0;
    private DateTime _lastProgressMovementUtc = DateTime.UtcNow;
    private double _lastProgressMovementRatio = -1;
    private DateTime _lastFreezeDebugLogUtc = DateTime.MinValue;

    private void RenderProgressBar()
    {
        if (_isDraggingProgress || _currentMediaInfo == null) return;
        if (_isRewindAnimating) return;

        var frame = _progressEngine.GetUiFrame();
        bool isLiveStream = frame.Duration.TotalSeconds <= 0 && frame.State == ProgressState.Playing;

        if (frame.Duration.TotalSeconds <= 0 && (!frame.ShowIndeterminate || isLiveStream))
        {
            CurrentTimeText.Text = isLiveStream ? "" : "--:--";
            RemainingTimeText.Text = isLiveStream ? "LIVE" : "--:--";
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressSpringTargetRatio = 0;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            StopSpringRenderLoop();
            _lastRenderTime = DateTime.MinValue;
            _lastRenderedDuration = TimeSpan.Zero;
            _lastDisplayedSecond = -1;
            return;
        }

        double engineRatio = 0;
        if (frame.Duration.TotalSeconds > 0)
        {
            engineRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
            engineRatio = Math.Clamp(engineRatio, 0, 1);
        }

        if (_isCatchUpAnimating)
        {
            double ratioDiff = Math.Abs(engineRatio - _lastRenderedRatio);
            if (ratioDiff > 0.01)
            {
                StopCatchUpAnimation();

                _progressTargetRatio = engineRatio;
                _progressSpringTargetRatio = _progressDisplayRatio;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                _isSeekSpringActive = true;
                _seekSpringStartTime = DateTime.Now;
                _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
                StartSpringRenderLoop();
                CurrentTimeText.Text = FormatTime(frame.Position);
                _lastRenderedRatio = engineRatio;
            }
            else
            {
                _lastRenderedRatio = engineRatio;
                return;
            }
        }

        _lastRenderedRatio = engineRatio;

        if (frame.DurationJustChanged)
        {
            ProgressBarBg.Visibility = frame.Duration.TotalSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;

            _isSeekSpringActive = false;
            _springSettleFrames = 0;
            _progressVelocity = 0;
            StopSpringRenderLoop();

            _progressDisplayRatio = engineRatio;
            _progressTargetRatio = engineRatio;
            _progressSpringTargetRatio = engineRatio;
            ProgressBarScale.ScaleX = engineRatio;
            _lastRenderTime = DateTime.Now;
            _lastRenderedDuration = frame.Duration;
        }
        else if (_isSeekSpringActive)
        {
            if (!Spring.IsActive)
            {
                _isSeekSpringActive = false;
                _progressDisplayRatio = Spring.DisplayRatio;
                _progressVelocity = 0;
                _springSettleFrames = 0;
                RuntimeLog.Debug("PROGRESS-SPRING", () => $"settled at ratio={_progressDisplayRatio:F4} engineRatio={engineRatio:F4}");

                _progressDisplayRatio = engineRatio;
                ProgressBarScale.ScaleX = engineRatio;
                _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(1.5);
            }
            else
            {
                if (!Spring.IsHooked)
                {
                    StartSpringRenderLoop();
                }

                if (!_isClickSeekPending && DateTime.UtcNow >= _protectSpringTargetUntil)
                {
                    _progressTargetRatio = engineRatio;
                    if (Math.Abs(_progressTargetRatio - _progressSpringTargetRatio) > 0.12)
                    {
                        _seekSpringStartTime = DateTime.Now;
                    }
                }
                _lastRenderedDuration = frame.Duration;
                if (frame.Duration.TotalSeconds > 0)
                {
                    var pos = TimeSpan.FromSeconds(_progressDisplayRatio * frame.Duration.TotalSeconds);
                    int sec = (int)pos.TotalSeconds;
                    if (sec != _lastDisplayedSecond)
                    {
                        _lastDisplayedSecond = sec;
                        CurrentTimeText.Text = FormatTime(pos);
                        RemainingTimeText.Text = FormatDuration(frame.Duration);
                    }
                }
                return;
            }
        }

        if (!_isSeekSpringActive)
        {
            DateTime now = DateTime.Now;
            double dt = _lastRenderTime == DateTime.MinValue ? 0.016 : (now - _lastRenderTime).TotalSeconds;
            dt = Math.Clamp(dt, 0.001, 0.1);
            _lastRenderTime = now;
            _lastRenderedDuration = frame.Duration;

            bool isRealtimeProgressing = frame.State == ProgressState.Playing || frame.State == ProgressState.Seeking;
            double effectivePlaybackRate = 1.0;
            if (_currentMediaInfo != null &&
                !double.IsNaN(_currentMediaInfo.PlaybackRate) &&
                !double.IsInfinity(_currentMediaInfo.PlaybackRate) &&
                _currentMediaInfo.PlaybackRate > 0)
            {
                effectivePlaybackRate = Math.Clamp(_currentMediaInfo.PlaybackRate, 0.5, 3.0);
            }
            double rawTargetRatio = engineRatio;
            double rawRatioDiff = Math.Abs(rawTargetRatio - _progressDisplayRatio);
            double rawDiffSeconds = rawRatioDiff * frame.Duration.TotalSeconds;

            if (!_isDraggingProgress &&
                !_isRewindAnimating &&
                !_isCatchUpAnimating &&
                !_isClickSeekPending &&
                !_isSeekSpringActive &&
                frame.Duration.TotalSeconds > 0 &&
                _progressDisplayRatio > 0.005 &&
                DateTime.Now >= _suppressExternalSeekDetectionUntil)
            {
                bool playing = frame.State == ProgressState.Playing ||
                               frame.State == ProgressState.Seeking;
                bool forwardJump = rawTargetRatio > _progressDisplayRatio &&
                                   rawDiffSeconds >= 1.2;
                bool backwardJump = rawTargetRatio < _progressDisplayRatio &&
                                    rawDiffSeconds >= 0.6;

                bool isLikelyTrackSkip = _progressDisplayRatio > 0.92 &&
                                         rawTargetRatio < 0.05;

                bool isFalseZeroReport = rawTargetRatio < 0.001 &&
                                         _progressDisplayRatio > 0.05 &&
                                         playing;

                bool isSuspiciousBackward = backwardJump && playing &&
                                            rawDiffSeconds > 30.0 &&
                                            _progressDisplayRatio > 0.10;

                if (rawDiffSeconds >= 0.6)
                {
                    RuntimeLog.Log("PROGRESS-SEEK",
                        $"check from={_progressDisplayRatio:F4} to={rawTargetRatio:F4} " +
                        $"diffSec={rawDiffSeconds:F2} state={frame.State} playing={playing} " +
                        $"forward={forwardJump} backward={backwardJump} likelySkip={isLikelyTrackSkip} " +
                        $"falseZero={isFalseZeroReport} suspiciousBack={isSuspiciousBackward}");
                }

                if (playing && (forwardJump || backwardJump) && !isLikelyTrackSkip && !isFalseZeroReport && !isSuspiciousBackward)
                {
                    AnimateExternalSeekTo(rawTargetRatio, frame);
                    _suppressExternalSeekDetectionUntil = DateTime.Now.AddMilliseconds(800);
                    _lastRenderTime = now;
                    _lastRenderedDuration = frame.Duration;
                    return;
                }

                if (isFalseZeroReport || isSuspiciousBackward)
                {
                    _lastRenderTime = now;
                    _lastRenderedDuration = frame.Duration;
                    return;
                }
            }

            if (rawDiffSeconds >= SOURCE_SMOOTH_SECONDS)
            {
                RuntimeLog.Debug("PROGRESS-RENDER-JUMP", () =>
                    $"*** LARGE-RENDER-SNAP: display={_progressDisplayRatio:F4} -> target={rawTargetRatio:F4} " +
                    $"diffSec={rawDiffSeconds:F2}s direction={(rawTargetRatio < _progressDisplayRatio ? "BACKWARD" : "forward")} " +
                    $"state={frame.State} pos={frame.Position.TotalSeconds:F2}s dur={frame.Duration.TotalSeconds:F1}s");
                _progressDisplayRatio = rawTargetRatio;
                _progressTargetRatio = rawTargetRatio;
                _progressSpringTargetRatio = rawTargetRatio;
                _progressVelocity = 0;
                ProgressBarScale.ScaleX = _progressDisplayRatio;
            }
            else
            {
                _progressTargetRatio = rawTargetRatio;

                double backwardThreshold = 0.5;
                if (_currentMediaInfo != null)
                {
                    bool isBrowserSource = _currentMediaInfo.Platform is MediaPlatform.Browser or MediaPlatform.YouTube or MediaPlatform.SoundCloud ||
                                          (!string.IsNullOrEmpty(_currentMediaInfo.SourceAppId) &&
                                           ((_currentMediaInfo.SourceAppId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) ||
                                            (_currentMediaInfo.SourceAppId.Contains("edge", StringComparison.OrdinalIgnoreCase)) ||
                                            (_currentMediaInfo.SourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) ||
                                            (_currentMediaInfo.SourceAppId.Contains("brave", StringComparison.OrdinalIgnoreCase)) ||
                                            (_currentMediaInfo.SourceAppId.Contains("opera", StringComparison.OrdinalIgnoreCase))));

                    if (isBrowserSource)
                    {
                        backwardThreshold = 1.5;
                    }
                }

                bool isUserSeekWindow = frame.State == ProgressState.Seeking || DateTime.Now < _allowProgressBackwardRenderUntil;
                bool isPostSeekStabilization = DateTime.Now < _blockBackwardAfterSeekUntil;

                if (_progressTargetRatio < _progressDisplayRatio)
                {
                    double backwardSeconds = (_progressDisplayRatio - _progressTargetRatio) * frame.Duration.TotalSeconds;

                    if (isUserSeekWindow)
                    {
                        RuntimeLog.Debug("PROGRESS-RENDER-BACKWARD", () =>
                            $"allowed (user-seek): display={_progressDisplayRatio:F4} target={_progressTargetRatio:F4} backSec={backwardSeconds:F2}s");
                    }
                    else if (isPostSeekStabilization)
                    {
                        _progressTargetRatio = _progressDisplayRatio;
                    }
                    else if (isRealtimeProgressing)
                    {
                        bool isBrowserSourceForBackward = _currentMediaInfo != null &&
                            _currentMediaInfo.Platform is MediaPlatform.Browser or MediaPlatform.YouTube or MediaPlatform.SoundCloud;

                        double allowedBackward = isBrowserSourceForBackward ? 0.5 : 0.08;
                        if (backwardSeconds > allowedBackward)
                        {
                            RuntimeLog.Debug("PROGRESS-RENDER-BACKWARD", () =>
                                $"BLOCKED (playback): display={_progressDisplayRatio:F4} target={_progressTargetRatio:F4} " +
                                $"backSec={backwardSeconds:F2}s > allowed={allowedBackward:F2}s isBrowser={isBrowserSourceForBackward}");
                            _progressTargetRatio = _progressDisplayRatio;
                        }
                        else
                        {
                            RuntimeLog.Debug("PROGRESS-RENDER-BACKWARD", () =>
                                $"allowed (small): display={_progressDisplayRatio:F4} target={_progressTargetRatio:F4} " +
                                $"backSec={backwardSeconds:F2}s <= allowed={allowedBackward:F2}s");
                        }
                    }
                    else if (backwardSeconds > backwardThreshold)
                    {
                        double maxBackwardStepSeconds = 0.22;
                        double maxBackwardRatioStep = maxBackwardStepSeconds / frame.Duration.TotalSeconds;
                        double cappedTarget = Math.Max(_progressTargetRatio, _progressDisplayRatio - maxBackwardRatioStep);
                        RuntimeLog.Debug("PROGRESS-RENDER-BACKWARD", () =>
                            $"capped (paused): display={_progressDisplayRatio:F4} target={_progressTargetRatio:F4} " +
                            $"backSec={backwardSeconds:F2}s capped={cappedTarget:F4}");
                        _progressTargetRatio = cappedTarget;
                    }
                }

                _progressDisplayRatio = _progressTargetRatio;
            }

            _progressDisplayRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
            ProgressBarScale.ScaleX = _progressDisplayRatio;
        }

        if (!_isDraggingProgress && frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
        {
            if (_lastProgressMovementRatio < 0 || Math.Abs(_progressDisplayRatio - _lastProgressMovementRatio) > 0.0015)
            {
                _lastProgressMovementRatio = _progressDisplayRatio;
                _lastProgressMovementUtc = DateTime.UtcNow;
            }
            else
            {
                var stuckFor = DateTime.UtcNow - _lastProgressMovementUtc;
                var visualDrift = Math.Abs(engineRatio - _progressDisplayRatio);
                if (stuckFor.TotalMilliseconds > 900 && visualDrift > 0.02)
                {
                    _lastProgressMovementUtc = DateTime.UtcNow;
                }
            }
        }

        int currentSecond = (int)frame.Position.TotalSeconds;
        if (currentSecond != _lastDisplayedSecond)
        {
            _lastDisplayedSecond = currentSecond;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatDuration(frame.Duration);
        }
    }

    private string FormatTime(TimeSpan time) => MediaProgressHelpers.FormatTime(time);
    private string FormatDuration(TimeSpan duration) => duration.TotalSeconds > 0 ? FormatTime(duration) : "LIVE";

    #region Progress Bar Click and Drag to Seek

    private void ProgressBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentMediaInfo == null || !_currentMediaInfo.IsSeekEnabled || _currentMediaInfo.Duration.TotalSeconds <= 0) return;

        e.Handled = true;

        StopCatchUpAnimation();

        _isClickSeekPending = true;
        _mouseDownPoint = e.GetPosition(ProgressBarContainer);
        ProgressBarContainer.CaptureMouse();

        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        double ratio = _mouseDownPoint.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
        _progressTargetRatio = ratio;
        if (!_isSeekSpringActive)
        {
            _progressSpringTargetRatio = _progressDisplayRatio;
            _progressVelocity = 0;
        }
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _seekSpringStartTime = DateTime.Now;
        _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
        StartSpringRenderLoop();

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    private void ProgressBar_MouseMove(object sender, MouseEventArgs e)
    {
        e.Handled = true;

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!_isClickSeekPending && !_isDraggingProgress) return;

        var currentPos = e.GetPosition(ProgressBarContainer);

        if (_isClickSeekPending && !_isDraggingProgress)
        {
            double dist = Math.Abs(currentPos.X - _mouseDownPoint.X);
            double threshold = _isSeekSpringActive ? 25.0 : DRAG_THRESHOLD;
            if (dist < threshold) return;

            _isClickSeekPending = false;
            _isDraggingProgress = true;
            _isSeekSpringActive = false;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            StopSpringRenderLoop();
            StopCatchUpAnimation();
            MusicViz.IsBuffering = true;
        }

        if (_isDraggingProgress)
        {
            UpdateProgressFromMouse(e);
        }
    }

    private async void ProgressBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        bool wasDragging = _isDraggingProgress;
        bool wasClickSeek = _isClickSeekPending;
        bool prevExpanded = _isProgressBarExpanded;
        MusicViz.IsBuffering = false;

        _isDraggingProgress = false;
        _isClickSeekPending = false;

        ProgressBarContainer.ReleaseMouseCapture();

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (System.Action)(() =>
        {

            if (prevExpanded && !ProgressBarContainer.IsMouseOver)
            {

                _isProgressBarExpanded = false;
                AnimateProgressBarHover(false);
            }

        }));

        if (wasDragging || wasClickSeek)
        {
            if (wasClickSeek && !wasDragging)
            {
                var duration2 = _progressEngine.GetUiFrame().Duration;
                _dragSeekPosition = ClampSeekTarget(_dragSeekPosition, duration2);

                _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
                _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
                _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
                _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
                _progressEngine.NotifyUserSeek(_dragSeekPosition);

                _lastDisplayedSecond = (int)_dragSeekPosition.TotalSeconds;
                CurrentTimeText.Text = FormatTime(_dragSeekPosition);
                UpdateProgressTimerState();

                await _mediaService.SeekAsync(_dragSeekPosition);
            }
            else
            {
                await SeekToPosition(_dragSeekPosition);
            }
        }
    }

    private void UpdateProgressFromMouse(MouseEventArgs e)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        var position = e.GetPosition(ProgressBarContainer);
        double ratio = position.X / ProgressBarContainer.ActualWidth;
        ratio = Math.Clamp(ratio, 0, 1);

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _progressDisplayRatio = ratio;
        _progressTargetRatio = ratio;
        _progressSpringTargetRatio = ratio;
        _progressVelocity = 0;
        _springSettleFrames = 0;
        ProgressBarScale.ScaleX = ratio;

        _dragSeekPosition = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);

        CurrentTimeText.Text = FormatTime(_dragSeekPosition);
    }

    private void AnimateSeekProgressTo(double targetRatio)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        _progressTargetRatio = targetRatio;
        if (!_isSeekSpringActive)
        {
            _progressSpringTargetRatio = _progressDisplayRatio;
            _progressVelocity = 0;
        }
        _springSettleFrames = 0;
        _isSeekSpringActive = true;
        _seekSpringStartTime = DateTime.Now;
        _protectSpringTargetUntil = DateTime.UtcNow.AddSeconds(1.5);
        StartSpringRenderLoop();
    }

    private DispatcherTimer? _rewindTextTimer;
    private bool _isRewindAnimating = false;

    private void AnimateProgressRewindTo(double targetRatio)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
        double delta = fromRatio - targetRatio;

        StopRewindTextAnimation();

        if (delta <= 0.005)
        {
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            return;
        }

        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(220 + delta * 320, 260, 480));

        long seqAtStart = _trackChangeSequence;

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        anim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _lastDisplayedSecond = (int)GetPositionForRatio(targetRatio).TotalSeconds;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            StopRewindTextAnimation();
            _isRewindAnimating = false;
        };

        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressDisplayRatio = targetRatio;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        var startTime = DateTime.UtcNow;
        var totalMs = duration.TotalMilliseconds;
        _rewindTextTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _rewindTextTimer.Tick += (s, e) =>
        {
            double t = (DateTime.UtcNow - startTime).TotalMilliseconds / totalMs;
            if (t >= 1.0)
            {
                StopRewindTextAnimation();
                return;
            }
            double eased = 1 - Math.Pow(2, -6 * 10 * t);
            double maxEased = 1 - Math.Pow(2, -60);
            eased = Math.Clamp(eased / maxEased, 0, 1);
            double ratio = fromRatio + (targetRatio - fromRatio) * eased;
            var pos = GetPositionForRatio(ratio);
            int sec = (int)pos.TotalSeconds;
            if (sec != _lastDisplayedSecond)
            {
                _lastDisplayedSecond = sec;
                CurrentTimeText.Text = FormatTime(pos);
            }
        };
        _rewindTextTimer.Start();
    }

    private void StopRewindTextAnimation()
    {
        if (_rewindTextTimer != null)
        {
            _rewindTextTimer.Stop();
            _rewindTextTimer = null;
        }
    }

    private TimeSpan GetPositionForRatio(double ratio)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Clamp(ratio, 0, 1) * duration.TotalSeconds);
    }

    private async Task SeekToPosition(TimeSpan newPos)
    {
        var duration = _progressEngine.GetUiFrame().Duration;
        if (duration.TotalSeconds <= 0) return;

        newPos = ClampSeekTarget(newPos, duration);

        try
        {
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
            _progressEngine.NotifyUserSeek(newPos);

            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);
            AnimateSeekProgressTo(targetRatio);
            CurrentTimeText.Text = FormatTime(newPos);
            _lastDisplayedSecond = (int)newPos.TotalSeconds;

            await _mediaService.SeekAsync(newPos);

            UpdateProgressTimerState();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PROGRESS-SEEK", ex.ToString());
        }
    }

    private async Task SeekRelative(double seconds)
    {
        var frame = _progressEngine.GetUiFrame();
        var duration = frame.Duration;
        if (duration.TotalSeconds <= 0) return;

        var currentPos = frame.Position;
        var newPos = currentPos + TimeSpan.FromSeconds(seconds);

        newPos = ClampSeekTarget(newPos, duration);

        try
        {
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
            _progressEngine.NotifyUserSeek(newPos);

            double targetRatio = newPos.TotalSeconds / duration.TotalSeconds;
            targetRatio = Math.Clamp(targetRatio, 0, 1);

            AnimateExternalSeekTo(targetRatio, frame);

            CurrentTimeText.Text = FormatTime(newPos);
            _lastDisplayedSecond = (int)newPos.TotalSeconds;

            await _mediaService.SeekToAbsoluteAsync(newPos);

            UpdateProgressTimerState();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PROGRESS-SEEK-RELATIVE", ex.ToString());
        }
    }

    private void ProgressBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _isProgressBarExpanded = true;
        AnimateProgressBarHover(true);
    }

    private void ProgressBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDraggingProgress && !_isClickSeekPending)
        {
            _isProgressBarExpanded = false;
            AnimateProgressBarHover(false);
        }
    }

    #endregion

    #region Progress Bar Animation on Expand

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

        SetTimerEnabled(_progressTimer, shouldRunProgress);
        SetTimerEnabled(_lyricsTimer, shouldRunLyrics);
        SetTimerEnabled(_volumeSyncTimer, shouldRunVolumeSync);

        if (isExpanded)
        {
            InputMonitorService.Start();
        }
        else
        {
            InputMonitorService.Stop();
        }
    }

    private static void SetTimerEnabled(DispatcherTimer timer, bool shouldRun)
    {
        if (shouldRun == timer.IsEnabled) return;

        if (shouldRun)
            timer.Start();
        else
            timer.Stop();
    }

    #endregion

    #region Progress Catch-Up Animation

    private bool _isCatchUpAnimating = false;
    private DispatcherTimer? _catchUpTimer;
    private TimeSpan _catchUpTargetPosition = TimeSpan.Zero;

    private void StartProgressCatchUpAnimation()
    {
        if (_currentMediaInfo == null) return;

        var frame = _progressEngine.GetUiFrame();
        if (frame.Duration.TotalSeconds <= 0) return;

        double targetRatio = frame.Position.TotalSeconds / frame.Duration.TotalSeconds;
        targetRatio = Math.Clamp(targetRatio, 0, 1);

        if (_isRewindAnimating)
        {
            _isRewindAnimating = false;
            StopRewindTextAnimation();
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        }

        if (_isSeekSpringActive && Spring.IsActive)
        {
            return;
        }

        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);

        if (Math.Abs(fromRatio - targetRatio) < 0.005)
        {
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _progressVelocity = 0;
            CurrentTimeText.Text = FormatTime(frame.Position);
            RemainingTimeText.Text = FormatDuration(frame.Duration);
            return;
        }

        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;
        _progressVelocity = 0;
        ProgressBarScale.ScaleX = fromRatio;

        double gapRatio = Math.Abs(targetRatio - fromRatio);
        int durationMs = (int)Math.Clamp(400 + gapRatio * 300, 400, 650);
        var catchUpDuration = TimeSpan.FromMilliseconds(durationMs);
        var catchUpAnim = new DoubleAnimation(fromRatio, targetRatio, new Duration(catchUpDuration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(catchUpAnim, VNotch.Services.AnimationConfig.TargetFps);

        long seqAtStart = _trackChangeSequence;

        catchUpAnim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
        };

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, catchUpAnim);
        CurrentTimeText.Text = FormatTime(frame.Position);
        RemainingTimeText.Text = FormatDuration(frame.Duration);
    }

    private void StopCatchUpAnimation()
    {
        _isCatchUpAnimating = false;
        if (_catchUpTimer != null)
        {
            _catchUpTimer.Stop();
            _catchUpTimer = null;
        }
    }

    private void AnimateTrackChangeRewindToZero(double fromRatio, TimeSpan newDuration)
    {
        fromRatio = Math.Clamp(fromRatio, 0, 1);
        double targetRatio = 0;

        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;

        long seqAtStart = _trackChangeSequence;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(320 + fromRatio * 150, 340, 470));

        AnimateProgressTrackChangeAppearance(seqAtStart);

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        anim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = 0;
            _progressDisplayRatio = 0;
            _progressTargetRatio = 0;
            _progressSpringTargetRatio = 0;
            _lastRenderedRatio = 0;
            _lastDisplayedSecond = 0;
            _isSeekSpringActive = false;
            _isClickSeekPending = false;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            StopSpringRenderLoop();
            CurrentTimeText.Text = "0:00";
            RemainingTimeText.Text = FormatDuration(newDuration);
            StopRewindTextAnimation();
            _isRewindAnimating = false;
            _lastRenderTime = DateTime.Now;
        };

        _progressTargetRatio = 0;
        _progressSpringTargetRatio = 0;
        _progressDisplayRatio = 0;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = fromRatio;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    private void AnimateProgressTrackChangeAppearance(long seqAtStart)
    {
        double currentOpacity = ProgressBarContainer.Opacity;
        double currentScaleY = ProgressBarMainScale.ScaleY;

        ProgressBarContainer.BeginAnimation(OpacityProperty, null);
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var opacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = _dur600,
            FillBehavior = FillBehavior.Stop
        };
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(currentOpacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.68, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), _easeQuadIn));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.84, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), _easeExpOut6));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), _easeExpOut6));
        Timeline.SetDesiredFrameRate(opacity, VNotch.Services.AnimationConfig.TargetFps);

        var scaleY = new DoubleAnimationUsingKeyFrames
        {
            Duration = _dur600,
            FillBehavior = FillBehavior.Stop
        };
        scaleY.KeyFrames.Add(new DiscreteDoubleKeyFrame(currentScaleY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.48, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), _easeQuadIn));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.76, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), _easeExpOut6));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), _easeExpOut6));
        Timeline.SetDesiredFrameRate(scaleY, VNotch.Services.AnimationConfig.TargetFps);

        opacity.Completed += (_, _) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

            ProgressBarContainer.BeginAnimation(OpacityProperty, null);
            ProgressBarContainer.Opacity = 1;
            ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ProgressBarMainScale.ScaleY = 1;
        };

        ProgressBarContainer.BeginAnimation(OpacityProperty, opacity);
        ProgressBarMainScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void AnimateExternalSeekTo(double targetRatio, UiProgressFrame frame)
    {
        targetRatio = Math.Clamp(targetRatio, 0, 1);
        double fromRatio = Math.Clamp(_progressDisplayRatio, 0, 1);
        double delta = Math.Abs(fromRatio - targetRatio);

        StopRewindTextAnimation();

        if (delta <= 0.005)
        {
            RuntimeLog.Log("PROGRESS-SEEK", $"animate skipped (delta={delta:F4})");
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _progressVelocity = 0;
            _springSettleFrames = 0;
            _isSeekSpringActive = false;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            _lastRenderedRatio = targetRatio;
            return;
        }

        _isSeekSpringActive = false;
        _springSettleFrames = 0;
        _progressVelocity = 0;
        StopSpringRenderLoop();
        _isRewindAnimating = true;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(220 + delta * 320, 240, 420));

        RuntimeLog.Log("PROGRESS-SEEK",
            $"animate start from={fromRatio:F4} to={targetRatio:F4} delta={delta:F4} dur={duration.TotalMilliseconds:F0}ms");

        var anim = new DoubleAnimation(fromRatio, targetRatio, new Duration(duration))
        {
            EasingFunction = _easeExpOut6,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        long seqAtStart = _trackChangeSequence;

        anim.Completed += (s, e) =>
        {
            if (_trackChangeSequence != seqAtStart) return;

            RuntimeLog.Log("PROGRESS-SEEK", $"animate completed -> {targetRatio:F4}");
            ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBarScale.ScaleX = targetRatio;
            _progressDisplayRatio = targetRatio;
            _progressTargetRatio = targetRatio;
            _progressSpringTargetRatio = targetRatio;
            _lastRenderedRatio = targetRatio;
            _lastDisplayedSecond = (int)GetPositionForRatio(targetRatio).TotalSeconds;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
            StopRewindTextAnimation();
            _isRewindAnimating = false;
            _lastRenderTime = DateTime.Now;
            _blockBackwardAfterSeekUntil = DateTime.Now.AddSeconds(3.5);
        };

        _progressTargetRatio = targetRatio;
        _progressSpringTargetRatio = targetRatio;

        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressBarScale.ScaleX = fromRatio;
        ProgressBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        var startTime = DateTime.UtcNow;
        var totalMs = duration.TotalMilliseconds;
        _rewindTextTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _rewindTextTimer.Tick += (s, e) =>
        {
            double t = (DateTime.UtcNow - startTime).TotalMilliseconds / totalMs;
            if (t >= 1.0)
            {
                CurrentTimeText.Text = FormatTime(GetPositionForRatio(targetRatio));
                StopRewindTextAnimation();
                return;
            }
            double eased = 1 - Math.Pow(2, -6 * 10 * t);
            double current = fromRatio + (targetRatio - fromRatio) * eased;
            CurrentTimeText.Text = FormatTime(GetPositionForRatio(current));
        };
        _rewindTextTimer.Start();
    }

    private double EaseCubicInOut(double t)
    {
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    #endregion
}
