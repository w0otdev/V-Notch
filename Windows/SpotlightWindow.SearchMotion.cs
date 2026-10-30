using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private bool _searchActivityActive;
    private bool _searchActivityRotating;
    private bool _searchInputPending;

    private void SetSearchActivity(bool active)
    {
        if (_searchActivityActive == active) return;
        _searchActivityActive = active;
        SearchActivity.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.BeginAnimation(OpacityProperty, null);
        StatusPanel.Opacity = 1;
        if (active && !AnimationConfig.ReduceMotion)
        {
            // Fade only on entering the waiting state, never on each keystroke.
            var fade = CreateAnimation(0, 1, TimeSpan.FromMilliseconds(150),
                CreateContentResizeEase());
            fade.FillBehavior = FillBehavior.Stop;
            StatusPanel.BeginAnimation(OpacityProperty, fade);
        }
        RefreshSearchActivityMotion();
    }

    private void RefreshSearchActivityMotion()
    {
        bool rotate = _searchActivityActive && SearchActivity.IsVisible && !AnimationConfig.ReduceMotion;
        if (_searchActivityRotating == rotate) return;
        _searchActivityRotating = rotate;
        SearchActivityRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SearchActivityRotation.Angle = 0;
        if (!rotate) return;

        // Constant velocity makes progress legible without pulsing the text.
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(turn, Math.Min(60, AnimationConfig.TargetFps));
        SearchActivityRotation.BeginAnimation(RotateTransform.AngleProperty, turn);
    }

    private void OnSearchMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnSearchMotionPreferenceChanged);
            return;
        }
        if (AnimationConfig.ReduceMotion)
        {
            StatusPanel.BeginAnimation(OpacityProperty, null);
            StatusPanel.Opacity = 1;
        }
        RefreshSearchActivityMotion();
    }
}
