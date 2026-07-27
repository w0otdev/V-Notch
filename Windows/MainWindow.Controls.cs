using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    #region Media Controls

    private bool _isPlaying = true;

    private async void PlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            _isPlaying = !_viewModel.IsPlaying;
            UpdatePlayPauseIcon();
            PlayButtonPressAnimation(PlayPauseButton);

            _progressEngine.NotifyUserPlayPause(_isPlaying);
            await _viewModel.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PlayPause failed");
        }
    }

    private async void NextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            PlayButtonPressAnimation(NextButton);
            PlayNextSkipAnimation();

            OptimisticPrepareForNextTrack();
            await _viewModel.NextTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "NextTrack failed");
        }
    }

    private async void PrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            PlayButtonPressAnimation(PrevButton);
            PlayPrevSkipAnimation();

            PrepareForPreviousTrackRequest();
            await _viewModel.PreviousTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "PrevTrack failed");
        }
    }

    private void UpdatePlayPauseIcon()
    {
        var duration = TimeSpan.FromMilliseconds(180);

        if (_isPlaying)
        {
            AnimateIconSwitch(PlayIcon, PauseIcon, duration, _easeQuadInOut);
            AnimateIconSwitch(InlinePlayIcon, InlinePauseIcon, duration, _easeQuadInOut);
        }
        else
        {
            AnimateIconSwitch(PauseIcon, PlayIcon, duration, _easeQuadInOut);
            AnimateIconSwitch(InlinePauseIcon, InlinePlayIcon, duration, _easeQuadInOut);
        }
    }

    private async void InlinePlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            _isPlaying = !_viewModel.IsPlaying;
            UpdatePlayPauseIcon();
            PlayButtonPressAnimation(InlinePlayPauseButton);

            _progressEngine.NotifyUserPlayPause(_isPlaying);

            await _viewModel.PlayPauseCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlinePlayPause failed");
        }
    }

    private async void InlineNextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            PlayButtonPressAnimation(InlineNextButton);
            PlayNextSkipAnimation(InlineNextArrow0, InlineNextArrow1, InlineNextArrow2);

            OptimisticPrepareForNextTrack();
            await _viewModel.NextTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlineNext failed");
        }
    }

    private async void InlinePrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            PlayButtonPressAnimation(InlinePrevButton);
            PlayPrevSkipAnimation(InlinePrevArrow0, InlinePrevArrow1, InlinePrevArrow2);

            PrepareForPreviousTrackRequest();
            await _viewModel.PreviousTrackCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-CTRL", ex, "InlinePrev failed");
        }
    }

    private void PrepareForPreviousTrackRequest()
    {
        try
        {
            // Previous is only a request: some players ignore it or expose the
            // control while declining the command. Keep rendering the confirmed
            // timeline until SMTC reports an actual restart/track change.
            _allowProgressBackwardRenderUntil = DateTime.Now.AddSeconds(3);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
            _progressEngine.NotifyPreviousTrackRequested();
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-PREV-PREP", ex.Message);
        }
    }

    private void OptimisticPrepareForNextTrack()
    {
        try
        {
            var frame = _progressEngine.GetUiFrame();
            _progressEngine.NotifyUserSeek(frame.Position);
            _suppressExternalSeekDetectionUntil = DateTime.Now.AddSeconds(3);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("PROGRESS-NEXT-PREP", ex.Message);
        }
    }

    private void ThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void CompactThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.EnableGestureControls && !_isExpanded && !_isMusicExpanded &&
            _currentMediaInfo != null && _currentMediaInfo.IsAnyMediaPlaying && !_isAnimating)
        {
            return;
        }

        e.Handled = true;
        if (!_isExpanded && !_isAnimating)
        {
            ExpandNotch();
        }
    }

    private void OpenCurrentMediaSourceFromThumbnail()
    {
        var info = _currentMediaInfo;
        if (info == null || !info.IsAnyMediaPlaying) return;
        Task.Run(() => MediaWindowActivator.TryActivateForMedia(info))
            .SafeFireAndForget("MEDIA-ACTIVATE");
    }

    private void SendMediaKey(byte key)
    {
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void MediaButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            button.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);

            var animX = new DoubleAnimation(transform.ScaleX, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.18, _dur150) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    private void MediaButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border button && button.RenderTransform is ScaleTransform transform)
        {
            var animX = new DoubleAnimation(transform.ScaleX, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            var animY = new DoubleAnimation(transform.ScaleY, 1.0, _dur200) { EasingFunction = _easeQuadOut };
            Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
            Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);

            animX.Completed += (_, _) =>
            {
                if (!button.IsMouseOver)
                    button.CacheMode = null;
            };

            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
    }

    #endregion

    #region Volume Control

    private float _currentVolume = 0.5f;
    private const float VolumeScrollStep = 0.05f;
    private const double CompactVolumeImmediateWheelDelta = 120.0;
    private const double CompactVolumeSmoothActivationDelta = 240.0;
    private const double CompactVolumeWheelSessionResetMs = 450.0;
    private const double CompactVolumeClickSuppressMs = 600.0;
    private DispatcherTimer? _volumeIndicatorHideTimer;
    private bool _isVolumeIndicatorActive = false;
    private bool _volumeSynced = false;
    private int _volumeIndicatorToken = 0;
    private double _compactVolumeWheelAccumulator = 0;
    private DateTime _lastCompactVolumeWheelUtc = DateTime.MinValue;
    private DateTime _suppressCompactVolumeWheelUntilUtc = DateTime.MinValue;
    private bool _isCompactVolumeWheelActive = false;

    private void SuppressCompactVolumeWheelForClick()
    {
        _suppressCompactVolumeWheelUntilUtc = DateTime.UtcNow.AddMilliseconds(CompactVolumeClickSuppressMs);
        ResetCompactVolumeWheelIntent();
    }

    private void ResetCompactVolumeWheelIntent()
    {
        _compactVolumeWheelAccumulator = 0;
        _lastCompactVolumeWheelUtc = DateTime.MinValue;
        _isCompactVolumeWheelActive = false;
    }

    private bool TryGetCompactVolumeWheelDelta(int rawDelta, out int volumeDelta)
    {
        volumeDelta = 0;
        if (rawDelta == 0) return false;

        var now = DateTime.UtcNow;
        if (now < _suppressCompactVolumeWheelUntilUtc)
        {
            ResetCompactVolumeWheelIntent();
            return false;
        }

        if ((now - _lastCompactVolumeWheelUtc).TotalMilliseconds > CompactVolumeWheelSessionResetMs)
        {
            _compactVolumeWheelAccumulator = 0;
            _isCompactVolumeWheelActive = false;
        }
        _lastCompactVolumeWheelUtc = now;

        if (_isVolumeIndicatorActive || _isCompactVolumeWheelActive)
        {
            _isCompactVolumeWheelActive = true;
            volumeDelta = rawDelta;
            return true;
        }

        if (Math.Abs(rawDelta) >= CompactVolumeImmediateWheelDelta)
        {
            _isCompactVolumeWheelActive = true;
            volumeDelta = rawDelta;
            return true;
        }

        if (_compactVolumeWheelAccumulator != 0 &&
            Math.Sign(_compactVolumeWheelAccumulator) != Math.Sign(rawDelta))
        {
            _compactVolumeWheelAccumulator = 0;
        }

        _compactVolumeWheelAccumulator += rawDelta;
        if (Math.Abs(_compactVolumeWheelAccumulator) < CompactVolumeSmoothActivationDelta)
        {
            return false;
        }

        _compactVolumeWheelAccumulator = 0;
        _isCompactVolumeWheelActive = true;
        volumeDelta = Math.Sign(rawDelta) * (int)CompactVolumeImmediateWheelDelta;
        return true;
    }

    private bool _volumeBaselinePending;

    private void AdjustVolumeByScroll(int delta)
    {
        if (!_volumeSynced)
        {
            // Read the baseline volume off the UI thread. The first (cold) COM
            // query enumerates audio sessions and scans processes, which can stall
            // the UI thread for tens of ms. That stalls the LiquidGlass present
            // (it marshals each frame back onto the UI thread), showing up as a
            // glass flicker on first boot. Defer applying this step until the real
            // baseline is known so the volume bar doesn't jump from a stale value.
            if (_volumeBaselinePending) return;
            _volumeBaselinePending = true;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = _mediaService.TryGetCurrentSessionVolume(out float vol, out bool _);
                Dispatcher.BeginInvoke(() =>
                {
                    _volumeBaselinePending = false;
                    if (ok) _currentVolume = vol;
                    _volumeSynced = true;
                    ApplyVolumeStep(delta);
                });
            });
            return;
        }

        ApplyVolumeStep(delta);
    }

    private void ApplyVolumeStep(int delta)
    {
        float step = (delta / 120f) * VolumeScrollStep;
        float newVolume = Math.Clamp(_currentVolume + step, 0f, 1f);
        _currentVolume = newVolume;

        ShowVolumeIndicator(newVolume);

        float volumeToSet = newVolume;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _mediaService.TrySetCurrentSessionVolume(volumeToSet);
        });

        if (_isExpanded)
        {
            VolumeBarScale.ScaleX = newVolume;
            UpdateVolumeIcon(newVolume, false);
        }
    }
    private void ShowVolumeIndicator(float volume)
    {
        if (VolumeIndicatorContainer == null || VolumeIndicatorFill == null) return;
        if (!_isMusicCompactMode) return;
        if (_isExpanded || _isAnimating) return;

        if (!_isVolumeIndicatorActive)
        {
            if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Volume, out int token))
            {
                return;
            }
            _volumeIndicatorToken = token;
        }

        if (!_isVolumeIndicatorActive)
        {
            _isVolumeIndicatorActive = true;

            SuppressPrivacyDot();

            if (_isCompactThumbnailHovered)
            {
                _isCompactThumbnailHovered = false;
                _compactThumbnailHoverLeaveTimer.Stop();

                NotchBorder.BeginAnimation(HeightProperty, null);
                AnimateCompactWidth(_collapsedWidth + 20, _dur400, _easeExpOut6, _volumeIndicatorToken);
                var heightAnim = MakeAnim(_collapsedHeight, _dur400, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                NotchBorder.BeginAnimation(HeightProperty, heightAnim);

                var thumbScaleAnim = MakeAnim(1.0, _dur350, _easeExpOut6, VNotch.Services.AnimationConfig.TargetFps);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, thumbScaleAnim);
                CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, thumbScaleAnim);

                CompactHoverInfo.BeginAnimation(OpacityProperty, null);
                var hoverFadeOut = MakeAnim(0.0, _dur200, _easeQuadOut);
                hoverFadeOut.Completed += (s, e) => CompactHoverInfo.Visibility = Visibility.Collapsed;
                CompactHoverInfo.BeginAnimation(OpacityProperty, hoverFadeOut);

                AnimateCornerRadius(_cornerRadiusCollapsed, TimeSpan.FromMilliseconds(400));
            }
            else
            {
                AnimateCompactWidth(_collapsedWidth + 20, _dur350, _easeExpOut6, _volumeIndicatorToken);
            }

            double initContainerWidth = _collapsedWidth - 32;
            VolumeIndicatorFill.Width = Math.Max(0, initContainerWidth * volume);

            MusicViz.BeginAnimation(OpacityProperty, null);
            var vizOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
            vizOut.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive)
                    MusicViz.Visibility = Visibility.Collapsed;
            };
            MusicViz.BeginAnimation(OpacityProperty, vizOut);

            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            var thumbOut = MakeAnim(1.0, 0.0, _dur200, _easeQuadOut);
            thumbOut.Completed += (s, e) =>
            {
                if (_isVolumeIndicatorActive)
                    CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            };
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbOut);

            VolumeIndicatorContainer.Visibility = Visibility.Visible;
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            var indicatorIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, indicatorIn);
        }

        double containerWidth = VolumeIndicatorContainer.ActualWidth;
        if (containerWidth <= 0)
        {
            containerWidth = _collapsedWidth - 32;
        }
        VolumeIndicatorFill.Width = Math.Max(0, containerWidth * volume);

        if (_volumeIndicatorHideTimer == null)
        {
            _volumeIndicatorHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _volumeIndicatorHideTimer.Tick += (s, e) =>
            {
                _volumeIndicatorHideTimer.Stop();
                ResetCompactVolumeWheelIntent();
                HideVolumeIndicator();
            };
        }
        _volumeIndicatorHideTimer.Stop();
        _volumeIndicatorHideTimer.Start();
    }
    private void HideVolumeIndicator()
    {
        if (VolumeIndicatorContainer == null) return;
        ResetCompactVolumeWheelIntent();
        int token = _volumeIndicatorToken;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        RestorePrivacyDotVisibility();

        if (_isExpanded || _isAnimating)
        {
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
            return;
        }

        AnimateCompactWidth(_collapsedWidth, _dur350, _easeExpOut6, 0);

        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
        var fadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        fadeOut.Completed += (s, e) =>
        {
            if (!_isVolumeIndicatorActive)
                VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        };
        VolumeIndicatorContainer.BeginAnimation(OpacityProperty, fadeOut);

        if (!_isClipboardPeekActive)
        {
            CompactThumbnailBorder.Visibility = Visibility.Visible;
            CompactThumbnailBorder.Opacity = 0;
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            var thumbIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, thumbIn);
        }

        if (!_isClipboardPeekActive)
        {
            ShowMusicVisualizer(duration: _dur100);
        }
    }

    private void DismissVolumeIndicatorImmediate()
    {
        _volumeIndicatorHideTimer?.Stop();
        ResetCompactVolumeWheelIntent();
        int token = _volumeIndicatorToken;

        _isVolumeIndicatorActive = false;
        _volumeSynced = false;
        _isDraggingVolumeIndicator = false;
        _compactPillArbiter.Release(token);
        _volumeIndicatorToken = 0;

        if (VolumeIndicatorContainer != null)
        {
            VolumeIndicatorContainer.ReleaseMouseCapture();
            VolumeIndicatorContainer.BeginAnimation(OpacityProperty, null);
            VolumeIndicatorContainer.Opacity = 0;
            VolumeIndicatorContainer.Visibility = Visibility.Collapsed;
        }

        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        }
        if (MusicViz != null)
        {
            MusicViz.BeginAnimation(OpacityProperty, null);
        }

        RestorePrivacyDotVisibility();
    }

    #region Volume Indicator Drag

    private bool _isDraggingVolumeIndicator = false;

    private void VolumeIndicator_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolumeIndicator = true;
        VolumeIndicatorContainer.CaptureMouse();
        SetVolumeFromIndicatorPosition(e);

        _volumeIndicatorHideTimer?.Stop();
    }

    private void VolumeIndicator_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingVolumeIndicator || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        SetVolumeFromIndicatorPosition(e);
    }

    private void VolumeIndicator_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingVolumeIndicator) return;
        e.Handled = true;
        _isDraggingVolumeIndicator = false;
        VolumeIndicatorContainer.ReleaseMouseCapture();

        _volumeIndicatorHideTimer?.Stop();
        _volumeIndicatorHideTimer?.Start();
    }

    private void SetVolumeFromIndicatorPosition(MouseEventArgs e)
    {
        var pos = e.GetPosition(VolumeIndicatorContainer);
        double containerWidth = VolumeIndicatorContainer.ActualWidth;
        if (containerWidth <= 0) containerWidth = _collapsedWidth - 32;

        float newVolume = (float)Math.Clamp(pos.X / containerWidth, 0.0, 1.0);
        _currentVolume = newVolume;

        VolumeIndicatorFill.Width = Math.Max(0, containerWidth * newVolume);

        float volumeToSet = newVolume;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _mediaService.TrySetCurrentSessionVolume(volumeToSet);
        });

        if (_isExpanded)
        {
            VolumeBarScale.ScaleX = newVolume;
            UpdateVolumeIcon(newVolume, false);
        }
    }

    #endregion

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_mediaService.TryToggleCurrentSessionMute())
        {
            SyncVolumeFromActiveSession();
        }
    }

    private void VolumeIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(VolumeIconScale.ScaleX, 1, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(VolumeIconScale.ScaleY, 1, _dur150) { EasingFunction = _easeQuadOut };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animX, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(animY, VNotch.Services.AnimationConfig.TargetFps);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolume = true;
        VolumeBarContainer.CaptureMouse();
        SetVolumeFromMousePosition(e);
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromMousePosition(e);
        }
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeBarContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetVolumeFromMousePosition(MouseEventArgs e)
    {
        const double volumeBarWidth = 100.0;
        var pos = e.GetPosition(VolumeBarContainer);
        float newVolume = (float)Math.Clamp(pos.X / volumeBarWidth, 0.0, 1.0);

        _currentVolume = newVolume;
        VolumeBarScale.ScaleX = newVolume;
        UpdateVolumeIcon(newVolume, false);

        float volumeToSet = newVolume;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            _mediaService.TrySetCurrentSessionVolume(volumeToSet);
        });
    }

    private void SyncVolumeFromActiveSession()
    {
        if (_isDraggingVolume) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDraggingVolume) return;

            if (_mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
            {
                _currentVolume = volume;
                VolumeBarScale.ScaleX = _currentVolume;
                UpdateVolumeIcon(_currentVolume, isMuted);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        _viewModel.UpdateVolumeState(volume, isMuted);
    }

    #endregion
}
