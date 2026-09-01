using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private void NotchWrapper_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isAudioView) return;

        if (!_isExpanded && !_isAnimating)
        {
            if (_settings.EnableHoverExpand) return;

            if (_isGestureActive)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            if (TryGetCompactVolumeWheelDelta(e.Delta, out int volumeDelta))
            {
                AdjustVolumeByScroll(volumeDelta);
            }
            return;
        }

        if (!_isExpanded || _isAnimating) return;
        if (e.Handled) return;

        e.Handled = true;

        ResetScrollSessionTimer();
        if (_isScrollSessionLocked) return;

        if ((DateTime.UtcNow - _lastViewSwitchUtc) < ViewSwitchCooldown) return;

        if (e.Delta < 0)
        {
            if (!_isSecondaryView && !_isTimerView)
            {
                SwitchToSecondaryView();
            }
            else if (_isSecondaryView && !_isTimerView)
            {
                StopCameraPreviewForViewExit();
                SwitchFromSecondaryToTimerView();
            }
        }
        else if (e.Delta > 0)
        {
            if (_isTimerView)
            {
                SwitchFromTimerToSecondaryView();
            }
            else if (_isSecondaryView)
            {
                StopCameraPreviewForViewExit();
                SwitchToPrimaryView();
            }
        }
    }

    private void ResetScrollSessionTimer()
    {
        if (_scrollSessionResetTimer == null)
        {
            _scrollSessionResetTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _scrollSessionResetTimer.Tick += (s, e) =>
            {
                _scrollSessionResetTimer.Stop();
                _isScrollSessionLocked = false;
            };
        }
        _scrollSessionResetTimer.Stop();
        _scrollSessionResetTimer.Start();
    }

    private void SecondaryContent_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void NavIconsPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void HomeIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAudioView && !_isAnimating)
        {
            SwitchFromAudioToPrimaryView();
        }
        else if (_isTimerView && !_isAnimating)
        {
            SwitchFromTimerToPrimaryView();
        }
        else if (_isSecondaryView && !_isAnimating)
        {
            StopCameraPreviewForViewExit();
            SwitchToPrimaryView();
        }
    }

    private void FileShelfIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isAudioView && !_isAnimating)
        {
            SwitchFromAudioToSecondaryView();
        }
        else if (_isTimerView && !_isAnimating)
        {
            SwitchFromTimerToSecondaryView();
        }
        else if (!_isSecondaryView && !_isAnimating)
        {
            SwitchToSecondaryView();
        }
    }

    private void SwitchToSecondaryView()
    {
        if (_isSecondaryView || _isAnimating) return;
        int generation = NextViewTransitionGeneration();
        _isSecondaryView = true;
        _isAnimating = true;
        SuspendSpotifyCanvasLifecycle();
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        HideMediaBackground();
        if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }

        UpdateShelfCapacityIndicator();

        UpdateNavIconsActiveState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;

        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Visible;
        var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easePowerOut3,
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Timeline.SetDesiredFrameRate(navBgFadeIn, VNotch.Services.AnimationConfig.TargetFps);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(1, 1);
        var primaryTranslate = new TranslateTransform(0, ExpandedContentRestY);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
        var slideUp = MakeAnim(ExpandedContentRestY, ExpandedContentRestY - 10, durOut, _easeAppleIn);
        var scaleDownX = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        var scaleDownY = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        Timeline.SetDesiredFrameRate(slideUp, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var expandedBlur = ExpandedContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        ExpandedContent.Effect = expandedBlur;
        var blurOutAnim = MakeAnim(0, _settings.EnableBlurEffects ? 6 : 0, durOut, _easeAppleIn);

        fadeOut.Completed += (s, e) =>
        {
            if (generation != _viewTransitionGeneration) return;
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null;
            expandedBlur.Radius = 0;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        expandedBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        SecondaryContent.Visibility = Visibility.Visible;
        SecondaryContent.BeginAnimation(OpacityProperty, null);
        SecondaryContent.Opacity = 0;
        EnableKeyboardInput();

        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(0.96, 0.96);
        var secondaryTranslate = new TranslateTransform(0, 16);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);
        // Hidden views may have received data updates without a layout pass.
        // Keep the root live and prepare it while transparent so the first
        // animated frame contains current content instead of a cached surface.
        SecondaryContent.UpdateLayout();

        var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        var springSlide = MakeAnim(16, 0, durIn, _easeAppleOut, inDelay);
        var springScaleX = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        var springScaleY = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, e) =>
        {
            if (generation != _viewTransitionGeneration) return;
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            SecondaryContent.Opacity = 1;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.RenderTransform = null;

            if (_pendingFlipThumbnail != null)
            {
                var thumb = _pendingFlipThumbnail;
                _pendingFlipThumbnail = null;
                AnimateThumbnailSwitchOnly(thumb, force: true);
            }

            if (IsCameraPreviewLifecycleActive)
            {
                StopCameraPreviewForViewExit();
            }
            ResetCameraSectionLayoutInstant();
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeIn);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    private void SwitchToPrimaryView()
    {
        if (!_isSecondaryView || _isAnimating) return;
        int generation = NextViewTransitionGeneration();
        _isSecondaryView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;
        _isScrollSessionLocked = true;

        if (IsCameraPreviewLifecycleActive)
        {
            StopCameraPreviewForViewExit();
        }
        else
        {
            ResetCameraSectionLayoutInstant();
        }

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        NotchBorder.IsHitTestVisible = false;

        var durOut = new Duration(TimeSpan.FromMilliseconds(170));
        var durIn = new Duration(TimeSpan.FromMilliseconds(440));
        var inDelay = TimeSpan.FromMilliseconds(40);
        int fps = VNotch.Services.AnimationConfig.TargetFps;

        var secondaryGroup = new TransformGroup();
        var secondaryScale = new ScaleTransform(1, 1);
        var secondaryTranslate = new TranslateTransform(0, 0);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut = MakeAnim(1, 0, durOut, _easeAppleIn);
        var slideDown = MakeAnim(0, 10, durOut, _easeAppleIn);
        var scaleDownX = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        var scaleDownY = MakeAnim(1, 0.96, durOut, _easeAppleIn);
        Timeline.SetDesiredFrameRate(slideDown, fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        var secondaryBlur = SecondaryContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        SecondaryContent.Effect = secondaryBlur;
        var blurOutAnim = MakeAnim(0, _settings.EnableBlurEffects ? 6 : 0, durOut, _easeAppleIn);

        fadeOut.Completed += (s, e) =>
        {
            if (generation != _viewTransitionGeneration) return;
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;
            SecondaryContent.Effect = null;
            secondaryBlur.Radius = 0;
            DisableKeyboardInput();
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeOut);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        secondaryBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        ExpandedContent.Opacity = 0;
        ExpandedContent.Effect = null;

        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(0.96, 0.96);
        var primaryTranslate = new TranslateTransform(0, ExpandedContentRestY - 16);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        PrepareExpandedContentLayoutForReveal();

        var fadeIn = MakeAnim(0, 1, durIn, _easeAppleOut, inDelay);
        var springSlide = MakeAnim(ExpandedContentRestY - 16, ExpandedContentRestY, durIn, _easeAppleOut, inDelay);
        var springScaleX = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        var springScaleY = MakeAnim(0.96, 1, durIn, _easeAppleOut, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(springSlide, fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);

        fadeIn.Completed += (s, e) =>
        {
            if (generation != _viewTransitionGeneration) return;
            _isAnimating = false;
            _isScrollSessionLocked = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            RestoreExpandedContentRestLayout();
            ResumeSpotifyCanvasLifecycle();

            ShowMediaBackground();

            if (_settings.EnableBlurEffects && !IsLiquidGlassEnabled && _isLyricsActive && !_isSpotifyCanvasMediaOpen && LyricsBlurBackground != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
                LyricsBlurBackground.Visibility = Visibility.Visible;
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                var lyricsBlurFadeIn = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(lyricsBlurFadeIn, VNotch.Services.AnimationConfig.TargetFps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, lyricsBlurFadeIn);
            }

            if (_pendingFlipThumbnail != null)
            {
                var thumb = _pendingFlipThumbnail;
                _pendingFlipThumbnail = null;
                AnimateThumbnailSwitchOnly(thumb, force: true);
            }
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeIn);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    private void UpdateNavIconsActiveState()
    {
        var showShelfCountBadge = false;

        if (_isAudioView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 1.0;
        }
        else if (_isTimerView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 1.0;
            AudioIconButton.Opacity = 0.4;
        }
        else if (_isSecondaryView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 1.0;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 0.4;
            showShelfCountBadge = ShelfUnlockBanner.Visibility != Visibility.Visible;
        }
        else
        {
            HomeIconButton.Opacity = 1.0;
            FileShelfIconButton.Opacity = 0.4;
            TimerIconButton.Opacity = 0.4;
            AudioIconButton.Opacity = 0.4;
        }

        if (!_isAnimating)
        {
            ShelfCountBadge.Visibility = showShelfCountBadge
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

}
