using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Controls;

internal static class ButtonFeedback
{
    private static readonly DependencyProperty TransitionVersionProperty = DependencyProperty.RegisterAttached(
        "TransitionVersion", typeof(int), typeof(ButtonFeedback), new PropertyMetadata(0));

    private static int NextTransitionVersion(DependencyObject target)
    {
        int next = ((int)target.GetValue(TransitionVersionProperty)) + 1;
        target.SetValue(TransitionVersionProperty, next);
        return next;
    }

    private static bool IsTransitionVersionCurrent(DependencyObject target, int version)
        => (int)target.GetValue(TransitionVersionProperty) == version;

    internal static void AnimateIconSwitch(Canvas fromIcon, Canvas toIcon, TimeSpan duration, EasingFunctionBase easing)
    {
        int fromVersion = NextTransitionVersion(fromIcon);
        _ = NextTransitionVersion(toIcon);

        var fromTransform = fromIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        fromIcon.RenderTransform = fromTransform;
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        fromIcon.BeginAnimation(UIElement.OpacityProperty, null);

        var toTransform = toIcon.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        toIcon.RenderTransform = toTransform;
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        toIcon.BeginAnimation(UIElement.OpacityProperty, null);

        fromIcon.Visibility = Visibility.Visible;
        fromTransform.ScaleX = 1;
        fromTransform.ScaleY = 1;
        fromIcon.Opacity = 1;

        toIcon.Visibility = Visibility.Visible;
        toTransform.ScaleX = 0.3;
        toTransform.ScaleY = 0.3;
        toIcon.Opacity = 0;

        var dur = new Duration(duration);
        var scaleDown = new DoubleAnimation(1, 0.3, dur) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easing };
        var scaleUp = new DoubleAnimation(0.3, 1, dur) { EasingFunction = easing };
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easing };

        var capturedFromIcon = fromIcon;
        var capturedFromTransform = fromTransform;

        fadeOut.Completed += (s, e) =>
        {
            if (!IsTransitionVersionCurrent(capturedFromIcon, fromVersion)) return;

            capturedFromTransform.ScaleX = 1;
            capturedFromTransform.ScaleY = 1;
            capturedFromIcon.Opacity = 1;

            capturedFromIcon.Visibility = Visibility.Collapsed;
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            capturedFromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            capturedFromIcon.BeginAnimation(UIElement.OpacityProperty, null);
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleDown, VNotch.Services.AnimationConfig.TargetFps);
        fromTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
        fromTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        fromIcon.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleUp, VNotch.Services.AnimationConfig.TargetFps);
        toTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        toTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        toIcon.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    internal static void PlayButtonPressAnimation(Border button)
    {
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        button.RenderTransform = transform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var squish = MakeAnim(1d, 0.82d, _dur80, _easeQuadIn, null);

        var bounce = new DoubleAnimation(0.82, 1.0, _dur250)
        {
            EasingFunction = _easeHapticBounce,
            BeginTime = TimeSpan.Zero
        };
        Timeline.SetDesiredFrameRate(bounce, VNotch.Services.AnimationConfig.TargetFps);

        squish.Completed += (s, e) =>
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, squish);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, squish);
    }

    internal static void PlayNextSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow1)
    {
        const double slideDistance = 220;
        int version = NextTransitionVersion(arrow1);

        var arrow1Transform = arrow1.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow1.RenderTransform = arrow1Transform;

        var slideOut1 = new DoubleAnimation(0, slideDistance, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut1 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(-slideDistance, 0, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        fadeOut1.Completed += (s, e) =>
        {
            if (!IsTransitionVersionCurrent(arrow1, version)) return;

            arrow1Transform.X = 0;
            arrow1.Opacity = 1;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow1Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow1.BeginAnimation(UIElement.OpacityProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(UIElement.OpacityProperty, null);
        };

        Timeline.SetDesiredFrameRate(slideOut1, VNotch.Services.AnimationConfig.TargetFps);
        arrow1Transform.BeginAnimation(TranslateTransform.XProperty, slideOut1);
        Timeline.SetDesiredFrameRate(fadeOut1, VNotch.Services.AnimationConfig.TargetFps);
        arrow1.BeginAnimation(UIElement.OpacityProperty, fadeOut1);
        Timeline.SetDesiredFrameRate(slideIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        Timeline.SetDesiredFrameRate(fadeIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0.BeginAnimation(UIElement.OpacityProperty, fadeIn0);
    }

    internal static void PlayPrevSkipAnimation(System.Windows.Shapes.Path arrow0, System.Windows.Shapes.Path arrow2)
    {
        const double slideDistance = 220;
        int version = NextTransitionVersion(arrow2);

        var arrow2Transform = arrow2.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow2.RenderTransform = arrow2Transform;

        var slideOut2 = new DoubleAnimation(0, -slideDistance, _dur250) { EasingFunction = _easeQuadOut };
        var fadeOut2 = new DoubleAnimation(1, 0, _dur250) { EasingFunction = _easeQuadOut };

        var arrow0Transform = arrow0.RenderTransform as TranslateTransform ?? new TranslateTransform();
        arrow0.RenderTransform = arrow0Transform;

        var slideIn0 = new DoubleAnimation(slideDistance, 0, _dur250) { EasingFunction = _easeQuadOut };
        var fadeIn0 = new DoubleAnimation(0, 1, _dur250) { EasingFunction = _easeQuadOut };

        fadeOut2.Completed += (s, e) =>
        {
            if (!IsTransitionVersionCurrent(arrow2, version)) return;

            arrow2Transform.X = 0;
            arrow2.Opacity = 1;
            arrow0Transform.X = 0;
            arrow0.Opacity = 0;

            arrow2Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow2.BeginAnimation(UIElement.OpacityProperty, null);
            arrow0Transform.BeginAnimation(TranslateTransform.XProperty, null);
            arrow0.BeginAnimation(UIElement.OpacityProperty, null);
        };

        Timeline.SetDesiredFrameRate(slideOut2, VNotch.Services.AnimationConfig.TargetFps);
        arrow2Transform.BeginAnimation(TranslateTransform.XProperty, slideOut2);
        Timeline.SetDesiredFrameRate(fadeOut2, VNotch.Services.AnimationConfig.TargetFps);
        arrow2.BeginAnimation(UIElement.OpacityProperty, fadeOut2);
        Timeline.SetDesiredFrameRate(slideIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0Transform.BeginAnimation(TranslateTransform.XProperty, slideIn0);
        Timeline.SetDesiredFrameRate(fadeIn0, VNotch.Services.AnimationConfig.TargetFps);
        arrow0.BeginAnimation(UIElement.OpacityProperty, fadeIn0);
    }
}

