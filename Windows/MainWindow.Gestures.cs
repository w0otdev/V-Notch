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

    private const string GestureLogTag = "GESTURE";
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

    // Synchronize translate transforms on notch wrapper and shadow wrapper so gesture
    // drags move the entire notch (including left/right ears) and its shadow together
    // without detaching curves or leaving shadow artifacts behind.
    private void EnsureGestureTransforms()
    {
        if (_gestureTranslate is not TranslateTransform)
        {
            _gestureTranslate = NotchGestureTranslate ?? FindTranslateTransform(NotchWrapper.RenderTransform);
            if (_gestureTranslate == null)
            {
                _gestureTranslate = new TranslateTransform(0, 0);
                if (NotchWrapper.RenderTransform is TransformGroup tg)
                {
                    tg.Children.Add(_gestureTranslate);
                }
                else
                {
                    var group = new TransformGroup();
                    group.Children.Add(NotchScale);
                    group.Children.Add(_gestureTranslate);
                    NotchWrapper.RenderTransform = group;
                }
            }

            if (NotchBorder.RenderTransform is TranslateTransform)
            {
                NotchBorder.RenderTransform = null;
            }
        }

        if (_gestureShadowTranslate is not TranslateTransform)
        {
            _gestureShadowTranslate = NotchShadowGestureTranslate ?? FindTranslateTransform(NotchShadowWrapper.RenderTransform);
            if (_gestureShadowTranslate == null)
            {
                _gestureShadowTranslate = new TranslateTransform(0, 0);
                if (NotchShadowWrapper.RenderTransform is TransformGroup tg)
                {
                    tg.Children.Add(_gestureShadowTranslate);
                }
                else
                {
                    var group = new TransformGroup();
                    group.Children.Add(NotchShadowScale);
                    group.Children.Add(_gestureShadowTranslate);
                    NotchShadowWrapper.RenderTransform = group;
                }
            }

            if (NotchBorderShadow.RenderTransform is TranslateTransform)
            {
                NotchBorderShadow.RenderTransform = null;
            }
        }
    }

    private static TranslateTransform? FindTranslateTransform(Transform transform)
    {
        if (transform is TranslateTransform tt) return tt;
        if (transform is TransformGroup tg)
        {
            foreach (var child in tg.Children)
            {
                if (child is TranslateTransform childTt) return childTt;
            }
        }
        return null;
    }

    private void InitializeGestureController()
    {
        _gestureController = new GestureController();
        _gestureController.SwipeLeft += OnGestureSwipeLeft;
        _gestureController.SwipeRight += OnGestureSwipeRight;
        _gestureController.SwipeDown += OnGestureSwipeDown;
        _gestureController.DoubleTap += OnGestureDoubleTap;
    }

    private async void NotchWrapper_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsScreenshotPillActive) return;
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_settings.EnableGestureControls) return;
        if (_isDraggingVolumeIndicator || _isDraggingNotchDebug) return;
        if (_spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;

        e.Handled = true;
        await TogglePlayPauseInstantAsync();
    }

    private void NotchWrapper_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsScreenshotPillActive) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
        }
    }

    private void NotchWrapper_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingNotchDebug && e.LeftButton == MouseButtonState.Pressed)
        {
            Win32Interop.GetCursorPos(out var pt);
            int dx = pt.X - (int)_debugDragStartMouse.X;
            int dy = pt.Y - (int)_debugDragStartMouse.Y;

            if (!_hasActuallyDragged && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
            {
                _hasActuallyDragged = true;
            }

            if (_hasActuallyDragged)
            {
                _fixedX = _debugDragStartFixedX + dx;
                _fixedY = _debugDragStartFixedY + dy;
                _overlayWindow.MoveFixedPosition(_fixedX, _fixedY);
                _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
            }

            e.Handled = true;
            return;
        }

        NotchBorder_GestureMouseMove(sender, e);
    }

    private void NotchWrapper_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingNotchDebug)
        {
            _isDraggingNotchDebug = false;
            NotchWrapper.ReleaseMouseCapture();

            if (!_hasActuallyDragged)
            {
                // User just clicked without dragging -> toggle/open notch normally
                ToggleNotchFromClick(e.ClickCount);
            }

            e.Handled = true;
            return;
        }

        NotchBorder_GestureMouseUp(sender, e);
    }

    private bool TryBeginGesture(MouseButtonEventArgs e)
    {
        if (!_settings.EnableGestureControls)
        {
            RuntimeLog.Log(GestureLogTag, "blocked: EnableGestureControls=false");
            return false;
        }
        if (_isAnimating)
        {
            RuntimeLog.Log(GestureLogTag, "blocked: _isAnimating=true");
            return false;
        }

        if (_isExpanded || _isMusicExpanded)
        {
            RuntimeLog.Log(GestureLogTag, $"blocked: expanded={_isExpanded} musicExpanded={_isMusicExpanded}");
            return false;
        }
        if (_currentMediaInfo == null || !_currentMediaInfo.IsAnyMediaPlaying)
        {
            RuntimeLog.Log(GestureLogTag, $"blocked: mediaInfo={(_currentMediaInfo != null)} isPlaying={_currentMediaInfo?.IsAnyMediaPlaying}");
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
        await Dispatcher.InvokeAsync(() =>
        {
            PlayGestureSwipeFeedback(isLeft: true);
            PlayNextSkipAnimation();
            OptimisticPrepareForNextTrack();
        });

        try
        {
            if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.UtcNow;

            await _mediaService.NextTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(GestureLogTag, ex, "SwipeLeft/Next failed");
        }
    }

    private async void OnGestureSwipeRight()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            PlayGestureSwipeFeedback(isLeft: false);
            PlayPrevSkipAnimation();
        });

        try
        {
            if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.UtcNow;

            PrepareForPreviousTrackRequest();
            await _mediaService.PreviousTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(GestureLogTag, ex, "SwipeRight/Prev failed");
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
        await Dispatcher.InvokeAsync(() =>
        {
            PlayGestureDoubleTapFeedback();
        });

        try
        {
            if ((DateTime.UtcNow - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.UtcNow;

            _isPlaying = !_isPlaying;
            await Dispatcher.InvokeAsync(() => UpdatePlayPauseIcon());

            _progressEngine.NotifyUserPlayPause(_isPlaying);
            await _mediaService.PlayPauseAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(GestureLogTag, ex, "DoubleTap/PlayPause failed");
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
        EnsureGestureTransforms();
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

    private void PlayGestureMiddleClickFeedback()
    {
        // Physics-based Squash & Stretch:
        // Height (ScaleY) compresses crisply, Width (ScaleX) expands organically (conservation of volume),
        // followed by a lively spring rebound and smooth settling. Highly satisfying Dynamic Island feel.
        var scaleY = new DoubleAnimationUsingKeyFrames();
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.94,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)),
            _easeQuadOut));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.028,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)),
            _easeQuadOut));
        scaleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(scaleY, VNotch.Services.AnimationConfig.TargetFps);

        var scaleX = new DoubleAnimationUsingKeyFrames();
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.022,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)),
            _easeQuadOut));
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame(0.988,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)),
            _easeQuadOut));
        scaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(scaleX, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
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
