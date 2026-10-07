using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private bool _clipboardDropHover;

    private void ResetClipboardDropFeedback()
    {
        _clipboardDropHover = false;
        ClipboardDropGlow.BeginAnimation(OpacityProperty, null);
        ClipboardDropBadge.BeginAnimation(OpacityProperty, null);
        ClipboardDropGlow.Opacity = ClipboardDropBadge.Opacity = 0;
        ClipboardDropBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardDropBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ClipboardDropBadgeScale.ScaleX = ClipboardDropBadgeScale.ScaleY = 1;
    }

    private void SetClipboardDropHover(bool active)
    {
        if (_clipboardDropHover == active) return;
        _clipboardDropHover = active;
        AnimateDropValue(ClipboardDropGlow, OpacityProperty, active ? 0.65 : 0, 140);
        if (active) AnimateDropValue(ClipboardDropBadge, OpacityProperty, 0, 140);
    }

    private void PlayClipboardDropFeedback()
    {
        if (_cleanedUp || !_isSecondaryView || _spotlightMorphSessionActive) return;
        _clipboardDropHover = false;
        // Confirmation follows the completed import, never a rejected or pending drop.
        AnimateDropValue(ClipboardDropGlow, OpacityProperty, 0, 250, from: 1);
        AnimateDropValue(ClipboardDropBadge, OpacityProperty, 0, 180, from: 1, delay: 650);
        if (AnimationConfig.ReduceMotion) return;
        PlayBadgeSpringBounce();
    }

    private void PlayBadgeSpringBounce()
    {
        ClipboardDropBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardDropBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ClipboardDropBadgeScale.ScaleX = ClipboardDropBadgeScale.ScaleY = 1.0;

        // Magnetic snap & spring bounce: snap from 0.85 -> overshoot to 1.035 -> settle to 1.0
        var animX = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        animX.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animX.KeyFrames.Add(new SplineDoubleKeyFrame(1.035, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), new KeySpline(0.2, 0.8, 0.25, 1.0)));
        animX.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new KeySpline(0.25, 1.0, 0.5, 1.0)));
        Timeline.SetDesiredFrameRate(animX, AnimationConfig.TargetFps);

        var animY = animX.Clone();
        ClipboardDropBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        ClipboardDropBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private static void AnimateDropValue(Animatable target, DependencyProperty property,
        double value, int duration, double? from = null, int delay = 0)
    {
        double current = from ?? (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        var animation = new DoubleAnimation(current, value,
            TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : duration))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = TrayMotion.EaseOut
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation);
    }

    private static void AnimateDropValue(FrameworkElement target, DependencyProperty property,
        double value, int duration, double? from = null, int delay = 0)
    {
        double current = from ?? (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, from.HasValue && delay > 0 ? current : value);
        var animation = new DoubleAnimation(current, value,
            TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : duration))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = TrayMotion.EaseOut
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        target.BeginAnimation(property, animation);
    }
}
