using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Privacy Indicators (Mic/Camera In-Use — Single Dot)

    private bool _privacyIndicatorsVisible = false;
    private PrivacyIndicatorState _lastPrivacyState = PrivacyIndicatorState.Empty;

    private const double PrivacyDotDefaultRightMargin = 27.0;
    private const double PrivacyDotCompactMusicRightMargin = 34.0;
    private const double PrivacyDotTopMargin = 11.2;
    private const double PrivacyDotSize = 7.0;

    private static readonly Color _micColor = Color.FromRgb(0xFF, 0x95, 0x00);
    private static readonly Color _camColor = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color _bothColor = Color.FromRgb(0x00, 0x4E, 0x92);
    private static readonly Color _screenRecColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    internal static bool ShouldSuppressPrivacyDot(
        bool isCompactPill,
        bool isAudioView,
        bool isVolumeIndicatorActive,
        bool isBluetoothNotificationVisible,
        bool isClipboardPeekActive)
        => !isCompactPill || isAudioView || isVolumeIndicatorActive ||
           isBluetoothNotificationVisible || isClipboardPeekActive;

    private bool IsCompactPillForPrivacyIndicator =>
        _notchState.CurrentState == NotchState.Collapsed &&
        !_isAnimating &&
        !_isGreetingActive;

    private bool IsPrivacyDotSuppressed => ShouldSuppressPrivacyDot(
        IsCompactPillForPrivacyIndicator,
        _isAudioView,
        _isVolumeIndicatorActive,
        _isBluetoothNotificationVisible,
        _isClipboardPeekActive);

    private void NotchState_PrivacyVisibilityChanged(object? sender, NotchStateChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(SyncPrivacyDotVisibilityForCurrentView);
            return;
        }

        SyncPrivacyDotVisibilityForCurrentView();
    }

    private void SyncPrivacyDotVisibilityForCurrentView()
    {
        if (!_privacyIndicatorsVisible) return;

        if (IsPrivacyDotSuppressed)
            SuppressPrivacyDot();
        else
            RestorePrivacyDotVisibility();
    }

    private void PrivacyModule_StateChanged(object? sender, PrivacyIndicatorState state)
    {
        Dispatcher.BeginInvoke(() => UpdatePrivacyIndicators(state));
    }

    private void UpdatePrivacyIndicators(PrivacyIndicatorState state)
    {
        if (!_settings.ShowSystemNotifications)
        {
            HidePrivacyIndicators();
            _lastPrivacyState = state;
            return;
        }

        if (_isCameraActive && state.CameraInUse)
        {
            state = state with
            {
                CameraInUse = false,
                CameraConsumers = Array.Empty<string>()
            };
        }

        bool shouldShow = state.AnyInUse;
        bool wasShowing = _privacyIndicatorsVisible;

        _lastPrivacyState = state;

        if (shouldShow && !wasShowing)
        {
            ShowPrivacyDot(state);
        }
        else if (!shouldShow && wasShowing)
        {
            HidePrivacyIndicators();
        }
        else if (shouldShow)
        {
            if (IsPrivacyDotSuppressed)
            {
                SuppressPrivacyDot();
            }
            else if (PrivacyIndicatorPanel.Visibility != Visibility.Visible)
            {
                ShowPrivacyDot(state);
            }
            else
            {
                ApplyDotColor(state, animate: true);
            }
        }
    }

    private void ShowPrivacyDot(PrivacyIndicatorState state)
    {
        _privacyIndicatorsVisible = true;

        if (IsPrivacyDotSuppressed)
            return;

        PrivacyDot.Visibility = Visibility.Visible;
        PrivacyIndicatorPanel.Visibility = Visibility.Visible;

        UpdatePrivacyDotPosition();

        ApplyDotColor(state, animate: false);

        var fadeIn = MakeAnim(0d, 1d, _dur350, _easeExpOut7, null);
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, fadeIn);

        AnimateDotIn(PrivacyDotScale);

        StartBreathingAnimation(PrivacyDot);

        RuntimeLog.Log("PRIVACY",
            $"Dot shown — Mic: {state.MicrophoneInUse}, Cam: {state.CameraInUse}, Screen: {state.ScreenRecordingActive}");
    }

    private void HidePrivacyIndicators()
    {
        if (!_privacyIndicatorsVisible) return;
        _privacyIndicatorsVisible = false;

        StopBreathingAnimation(PrivacyDot);

        var fadeOut = MakeAnim(1d, 0d, _dur250, _easePowerIn2, null);
        fadeOut.Completed += (_, _) =>
        {
            PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
            PrivacyDot.Visibility = Visibility.Collapsed;
        };
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, fadeOut);

        RuntimeLog.Log("PRIVACY", "Dot hidden");
    }

    private void SuppressPrivacyDot()
    {
        if (!_privacyIndicatorsVisible) return;

        StopBreathingAnimation(PrivacyDot);
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, null);
        PrivacyIndicatorPanel.Opacity = 0;
        PrivacyIndicatorPanel.Visibility = Visibility.Collapsed;
    }

    private void RestorePrivacyDotVisibility()
    {
        if (!_privacyIndicatorsVisible) return;
        if (IsPrivacyDotSuppressed) return;

        PrivacyDot.Visibility = Visibility.Visible;
        PrivacyIndicatorPanel.Visibility = Visibility.Visible;

        UpdatePrivacyDotPosition();

        ApplyDotColor(_lastPrivacyState, animate: false);

        var fadeIn = MakeAnim(0d, 1d, _dur250, _easePowerOut3, null);
        PrivacyIndicatorPanel.BeginAnimation(OpacityProperty, fadeIn);

        StartBreathingAnimation(PrivacyDot);
    }

    private void UpdatePrivacyDotPosition(bool animate = false)
    {
        bool compact = _isMusicCompactMode && !_isExpanded;
        double right = compact ? PrivacyDotCompactMusicRightMargin : PrivacyDotDefaultRightMargin;

        double top = PrivacyDotTopMargin;
        bool isCollapsed = !_isExpanded && !_isMusicExpanded;
        if (isCollapsed && _settings.EnableDynamicIslandMode)
        {
            double h = NotchBorder?.ActualHeight > 0
                ? NotchBorder.ActualHeight
                : GetCollapsedHeight();
            if (h > 0) top = Math.Max(0, (h - PrivacyDotSize) / 2.0);
        }

        var current = PrivacyIndicatorPanel.Margin;
        var target = new Thickness(0, top, right, 0);

        PrivacyIndicatorPanel.BeginAnimation(MarginProperty, null);
        PrivacyIndicatorPanel.Margin = target;

        if (animate && PrivacyIndicatorPanel.Visibility == Visibility.Visible &&
            (Math.Abs(current.Right - target.Right) > 0.5 || Math.Abs(current.Top - target.Top) > 0.5))
        {
            // Matches the 450ms pill width animation in ApplyMusicCompactMode so
            // the dot glides with the layout instead of snapping to its new spot.
            var slide = new ThicknessAnimation(current, target, new Duration(TimeSpan.FromMilliseconds(450)))
            {
                EasingFunction = _easeExpOut6,
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(slide, VNotch.Services.AnimationConfig.TargetFps);
            PrivacyIndicatorPanel.BeginAnimation(MarginProperty, slide);
        }
    }

    private void ApplyDotColor(PrivacyIndicatorState state, bool animate)
    {
        Color targetColor;
        if (state.ScreenRecordingActive)
            targetColor = _screenRecColor;
        else if (state.MicrophoneInUse && state.CameraInUse)
            targetColor = _bothColor;
        else if (state.CameraInUse)
            targetColor = _camColor;
        else
            targetColor = _micColor;

        if (animate)
        {
            var colorAnim = new ColorAnimation
            {
                To = targetColor,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = _easeSineInOut
            };
            Timeline.SetDesiredFrameRate(colorAnim, VNotch.Services.AnimationConfig.TargetFps);
            PrivacyDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
        else
        {
            PrivacyDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            PrivacyDotBrush.Color = targetColor;
        }
    }

    private static void AnimateDotIn(ScaleTransform scale)
    {
        var popIn = new DoubleAnimationUsingKeyFrames();
        popIn.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        popIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), _easeExpOut7));
        popIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(popIn, VNotch.Services.AnimationConfig.TargetFps);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, popIn);
    }

    private static void StartBreathingAnimation(FrameworkElement dot)
    {
        var breathing = new DoubleAnimation
        {
            From = 1.0,
            To = 0.4,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = _easeSineInOut
        };
        Timeline.SetDesiredFrameRate(breathing, VNotch.Services.AnimationConfig.TargetFps);
        dot.BeginAnimation(OpacityProperty, breathing);
    }

    private static void StopBreathingAnimation(FrameworkElement dot)
    {
        dot.BeginAnimation(OpacityProperty, null);
        dot.Opacity = 1.0;
    }

    #endregion
}
