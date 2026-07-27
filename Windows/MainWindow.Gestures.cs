using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Gesture Controls

    private GestureController _gestureController = null!;

    private bool _isGestureActive
    {
        get => _gestureController?.IsGestureActive ?? false;
        set
        {
            if (_gestureController == null || _gestureController.IsGestureActive == value) return;

            _gestureController.IsGestureActive = value;
            UpdateGlassMotionState();
        }
    }

    private TranslateTransform? _gestureTranslate;
    private TranslateTransform? _gestureShadowTranslate;

    // Lazily wire matching translate transforms onto the visible notch body and its
    // separate shadow shape so a gesture drag moves both as one. Without the shadow
    // side, the (now dark) drop shadow stays put and shows as a black crescent when
    // the pill slides over it.
    private void EnsureGestureTransforms()
    {
        if (_gestureTranslate is not TranslateTransform)
        {
            _gestureTranslate = NotchBorder.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
            NotchBorder.RenderTransform = _gestureTranslate;
        }
        if (_gestureShadowTranslate is not TranslateTransform)
        {
            _gestureShadowTranslate = NotchBorderShadow.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
            NotchBorderShadow.RenderTransform = _gestureShadowTranslate;
        }
    }

    private void InitializeGestureController()
    {
        _gestureController = new GestureController();
        _gestureController.SwipeLeft += OnGestureSwipeLeft;
        _gestureController.SwipeRight += OnGestureSwipeRight;
        _gestureController.SwipeDown += OnGestureSwipeDown;
        _gestureController.DoubleTap += OnGestureDoubleTap;
    }

    private void NotchWrapper_MouseMove(object sender, MouseEventArgs e)
    {
        NotchBorder_GestureMouseMove(sender, e);
    }

    private void NotchWrapper_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        NotchBorder_GestureMouseUp(sender, e);
    }

    private bool TryBeginGesture(MouseButtonEventArgs e)
    {
        if (!_settings.EnableGestureControls)
        {
            RuntimeLog.Log("GESTURE", "blocked: EnableGestureControls=false");
            return false;
        }
        if (_isAnimating)
        {
            RuntimeLog.Log("GESTURE", "blocked: _isAnimating=true");
            return false;
        }

        if (_isExpanded || _isMusicExpanded)
        {
            RuntimeLog.Log("GESTURE", $"blocked: expanded={_isExpanded} musicExpanded={_isMusicExpanded}");
            return false;
        }
        if (_currentMediaInfo == null || !_currentMediaInfo.IsAnyMediaPlaying)
        {
            RuntimeLog.Log("GESTURE", $"blocked: mediaInfo={(_currentMediaInfo != null)} isPlaying={_currentMediaInfo?.IsAnyMediaPlaying}");
            return false;
        }

        var pos = e.GetPosition(NotchBorder);
        _gestureController.BeginTracking(pos);
        _isGestureActive = true;

        NotchWrapper.CaptureMouse();

        return true;
    }

    private void NotchBorder_GestureMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isGestureActive || !_gestureController.IsTracking) return;

        var pos = e.GetPosition(NotchBorder);
        bool triggered = _gestureController.UpdateTracking(pos);

        if (!triggered && !_gestureController.GestureTriggered)
        {
            ApplyGestureDragFeedback(_gestureController.AccumulatedX);
        }
    }

    private void NotchBorder_GestureMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isGestureActive) return;

        NotchWrapper.ReleaseMouseCapture();
        _isGestureActive = false;

        var pos = e.GetPosition(NotchBorder);
        bool wasTap = _gestureController.EndTracking(pos);

        if (!_gestureController.GestureTriggered && !wasTap)
        {
            AnimateGestureSnapBack();
            ToggleNotchFromClick(e.ClickCount);
        }
        else if (!_gestureController.GestureTriggered && wasTap)
        {
            AnimateGestureSnapBack();
        }
        else
        {
            AnimateGestureSnapBack();
        }

        if (!NotchWrapper.IsMouseOver)
        {
            AnimateNotchHover(false);
            if (_isMusicCompactMode && _isCompactThumbnailHovered)
            {
                SetCompactThumbnailHover(false);
            }
        }
    }

    private async void OnGestureSwipeLeft()
    {
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeFeedback(isLeft: true);
            PlayNextSkipAnimation();
            OptimisticPrepareForNextTrack();
        });

        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            await _mediaService.NextTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("GESTURE", ex, "SwipeLeft/Next failed");
        }
    }

    private async void OnGestureSwipeRight()
    {
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeFeedback(isLeft: false);
            PlayPrevSkipAnimation();
        });

        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            PrepareForPreviousTrackRequest();
            await _mediaService.PreviousTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("GESTURE", ex, "SwipeRight/Prev failed");
        }
    }

    private void OnGestureSwipeDown()
    {
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeDownFeedback();

            if (!_isExpanded && !_isAnimating)
            {
                ExpandNotch();

                var waitTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(40)
                };
                waitTimer.Tick += (s, args) =>
                {
                    if (!_isAnimating)
                    {
                        waitTimer.Stop();
                        if (!_isSecondaryView)
                        {
                            SwitchToSecondaryView();
                        }
                    }
                };
                waitTimer.Start();
            }
        });
    }

    private async void OnGestureDoubleTap()
    {
        Dispatcher.Invoke(() =>
        {
            PlayGestureDoubleTapFeedback();
        });

        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            _isPlaying = !_isPlaying;
            Dispatcher.Invoke(() => UpdatePlayPauseIcon());

            _progressEngine.NotifyUserPlayPause(_isPlaying);
            await _mediaService.PlayPauseAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("GESTURE", ex, "DoubleTap/PlayPause failed");
        }
    }

    private void ApplyGestureDragFeedback(double deltaX)
    {
        double dampened = deltaX * 0.3;
        double clamped = Math.Clamp(dampened, -20, 20);

        EnsureGestureTransforms();

        _gestureTranslate!.X = clamped;
        _gestureShadowTranslate!.X = clamped;
    }

    private void AnimateGestureSnapBack()
    {
        if (_gestureTranslate == null) return;

        var snapBack = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(350)))
        {
            EasingFunction = _easeSoftSpring
        };
        Timeline.SetDesiredFrameRate(snapBack, VNotch.Services.AnimationConfig.TargetFps);
        BeginGlassGestureSnapBack(snapBack);
        snapBack.Completed += (s, e) =>
        {
            _gestureTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _gestureTranslate.X = 0;
            if (_gestureShadowTranslate != null)
            {
                _gestureShadowTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                _gestureShadowTranslate.X = 0;
            }
        };

        _gestureTranslate.BeginAnimation(TranslateTransform.XProperty, snapBack);
        _gestureShadowTranslate?.BeginAnimation(TranslateTransform.XProperty, snapBack);
    }

    private void PlayGestureSwipeFeedback(bool isLeft)
    {
        EnsureGestureTransforms();

        double target = isLeft ? -12 : 12;

        var flick = new DoubleAnimationUsingKeyFrames();
        flick.KeyFrames.Add(new EasingDoubleKeyFrame(target,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            _easeQuadOut));
        flick.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(flick, VNotch.Services.AnimationConfig.TargetFps);

        flick.Completed += (s, e) =>
        {
            _gestureTranslate!.BeginAnimation(TranslateTransform.XProperty, null);
            _gestureTranslate.X = 0;
            _gestureShadowTranslate!.BeginAnimation(TranslateTransform.XProperty, null);
            _gestureShadowTranslate.X = 0;
        };

        _gestureTranslate!.BeginAnimation(TranslateTransform.XProperty, flick);
        _gestureShadowTranslate!.BeginAnimation(TranslateTransform.XProperty, flick);

        var pulse = new DoubleAnimationUsingKeyFrames();
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.96,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            _easeQuadOut));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(pulse, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private void PlayGestureSwipeDownFeedback()
    {
        var pullDown = new DoubleAnimationUsingKeyFrames();
        pullDown.KeyFrames.Add(new EasingDoubleKeyFrame(1.08,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            _easeQuadOut));
        pullDown.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(pullDown, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pullDown);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pullDown);
    }

    private void PlayGestureDoubleTapFeedback()
    {
        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.92,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            _easeQuadIn));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.06,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
            _easeQuadOut));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounce, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private void DisposeGestureController()
    {
        if (_gestureController != null)
        {
            _gestureController.SwipeLeft -= OnGestureSwipeLeft;
            _gestureController.SwipeRight -= OnGestureSwipeRight;
            _gestureController.SwipeDown -= OnGestureSwipeDown;
            _gestureController.DoubleTap -= OnGestureDoubleTap;
        }
    }

    #endregion
}
