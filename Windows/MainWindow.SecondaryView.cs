using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Models;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private void NotchWrapper_MouseWheel(object sender, MouseWheelEventArgs e)
    {

        if (_isAudioView && e.OriginalSource is Visual v && AudioScrollViewer != null && v.IsDescendantOf(AudioScrollViewer))
        {
            return;
        }

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

        var activeTabs = GetActiveTabSequence();
        if (activeTabs.Count <= 1) return;

        NotchView currentView = NotchView.Media;
        if (_isCameraView) currentView = NotchView.Camera;
        else if (_isAudioView)
        {
            currentView = NotchView.AudioMixer;
        }
        else if (_isTimerView)
        {
            currentView = NotchView.Timer;
        }
        else if (_isSecondaryView)
        {
            currentView = NotchView.Secondary;
        }

        int currentIndex = activeTabs.IndexOf(currentView);
        if (currentIndex < 0) currentIndex = 0;

        int targetIndex = currentIndex;
        if (e.Delta < 0 && currentIndex < activeTabs.Count - 1)
        {
            targetIndex = currentIndex + 1;
        }
        else if (e.Delta > 0 && currentIndex > 0)
        {
            targetIndex = currentIndex - 1;
        }

        if (targetIndex != currentIndex)
        {
            NavigateToNotchView(activeTabs[targetIndex]);
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
        if (_isCameraView && !_isAnimating) { SwitchToPrimaryView(); return; }
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

    private void ClipboardIconButton_Click(object sender, MouseButtonEventArgs e)
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

    private void SwitchToSecondaryView(long? transitionId = null)
    {
        if (transitionId == null) _transitionCoordinator.RequestView(NotchView.Secondary, "SwitchToSecondaryView");
        else ExpandNotch(transitionId, targetView: NotchView.Secondary);
    }

    private void SwitchToPrimaryView(long? transitionId = null)
    {
        if (transitionId == null) _transitionCoordinator.RequestView(NotchView.Media, "SwitchToPrimaryView");
        else ExpandNotch(transitionId, targetView: NotchView.Media);
    }


    private void AnimateNavIconOpacity(FrameworkElement? icon, double targetOpacity, bool animate)
    {
        if (icon == null) return;

        // An old HoldEnd clock overrides local opacity, even when the icon
        // currently looks correct. Remove it before applying the new state.
        double currentOpacity = icon.Opacity;
        icon.BeginAnimation(UIElement.OpacityProperty, null);
        icon.Opacity = targetOpacity;

        if (!animate || Math.Abs(currentOpacity - targetOpacity) < 0.01)
            return;

        var anim = new DoubleAnimation
        {
            From = currentOpacity,
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = _easeAppleOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        icon.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private VNotch.Models.NotchView? _navigationVisualTarget;

    private void UpdateNavIconsActiveState(bool animate = true)
    {
        if (HomeIconButton == null || ClipboardIconButton == null || TimerIconButton == null || AudioIconButton == null)
            return;


        double homeTarget = 0.45;
        double clipboardTarget = 0.45;
        double timerTarget = 0.45;
        double audioTarget = 0.45;

        var target = _navigationVisualTarget ?? (_isCameraView ? NotchView.Camera : _isAudioView ? NotchView.AudioMixer :
            _isTimerView ? NotchView.Timer : _isSecondaryView ? NotchView.Secondary : NotchView.Media);
        homeTarget = target == NotchView.Media ? 1 : 0.45;
        clipboardTarget = target == NotchView.Secondary ? 1 : 0.45;
        timerTarget = target == NotchView.Timer ? 1 : 0.45;
        audioTarget = target == NotchView.AudioMixer ? 1 : 0.45;
        if (_navDragItem != CameraIconButton) AnimateNavIconOpacity(CameraIconButton, target == NotchView.Camera ? 1 : 0.45, animate);

        // Only update opacity on items not currently being dragged by the user
        if (_navDragItem != HomeIconButton) AnimateNavIconOpacity(HomeIconButton, homeTarget, animate);
        if (_navDragItem != ClipboardIconButton) AnimateNavIconOpacity(ClipboardIconButton, clipboardTarget, animate);
        if (_navDragItem != TimerIconButton) AnimateNavIconOpacity(TimerIconButton, timerTarget, animate);
        if (_navDragItem != AudioIconButton) AnimateNavIconOpacity(AudioIconButton, audioTarget, animate);


    }
}
