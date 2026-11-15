using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public sealed class AiSuggestionButton : Button
{
    private ScaleTransform? _scale;
    private TranslateTransform? _lift;
    private FrameworkElement? _hover;
    private FrameworkElement? _press;

    public AiSuggestionButton()
    {
        Loaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= OnMotionPreferenceChanged;
            AnimationConfig.ReduceMotionChanged += OnMotionPreferenceChanged;
            UpdateInteraction(animate: false);
        };
        Unloaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= OnMotionPreferenceChanged;
            ResetMotion();
        };
        IsVisibleChanged += (_, _) => UpdateInteraction(animate: false);
    }

    public override void OnApplyTemplate()
    {
        ResetMotion();
        base.OnApplyTemplate();
        _scale = new ScaleTransform();
        _lift = new TranslateTransform();
        if (GetTemplateChild("MotionSurface") is FrameworkElement surface)
        {
            // Own mutable transforms; template resources may be shared or frozen.
            surface.RenderTransform = new TransformGroup { Children = { _scale, _lift } };
        }
        _hover = GetTemplateChild("HoverOverlay") as FrameworkElement;
        _press = GetTemplateChild("PressedOverlay") as FrameworkElement;
        UpdateInteraction(animate: false);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty ||
            e.Property == IsKeyboardFocusedProperty || e.Property == IsEnabledProperty)
            UpdateInteraction(animate: IsLoaded && IsVisible);
    }

    private void UpdateInteraction(bool animate)
    {
        bool active = IsLoaded && IsVisible && IsEnabled;
        bool hover = active && (IsKeyboardFocused ||
            IsMouseOver && !AreAnyTouchesOver && Stylus.CurrentStylusDevice == null);
        bool pressed = active && IsPressed;
        bool reduced = AnimationConfig.ReduceMotion;
        double scale = reduced ? 1 : pressed ? 0.985 : hover ? 1.015 : 1;
        animate &= !reduced;
        Retarget(_scale, ScaleTransform.ScaleXProperty, scale, animate);
        Retarget(_scale, ScaleTransform.ScaleYProperty, scale, animate);
        Retarget(_lift, TranslateTransform.YProperty, !reduced && hover && !pressed ? -2 : 0, animate);
        Retarget(_hover, OpacityProperty, hover && !pressed ? 1 : 0, animate);
        Retarget(_press, OpacityProperty, pressed ? 1 : 0, animate);
    }

    private void OnMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnMotionPreferenceChanged);
            return;
        }
        UpdateInteraction(animate: false);
    }

    private void ResetMotion()
    {
        Retarget(_scale, ScaleTransform.ScaleXProperty, 1, false);
        Retarget(_scale, ScaleTransform.ScaleYProperty, 1, false);
        Retarget(_lift, TranslateTransform.YProperty, 0, false);
        Retarget(_hover, OpacityProperty, 0, false);
        Retarget(_press, OpacityProperty, 0, false);
    }

    private static void Retarget(IAnimatable? target, DependencyProperty property, double value, bool animate)
    {
        if (target is not DependencyObject dependency) return;
        double from = (double)dependency.GetValue(property);
        target.BeginAnimation(property, null);
        dependency.SetValue(property, value);
        if (!animate || Math.Abs(from - value) < 0.001) return;
        var transition = new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = CubicBezierEase.FromEaseOutCurve(0.23, 1, 0.32, 1),
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(transition, AnimationConfig.TargetFps);
        target.BeginAnimation(property, transition, HandoffBehavior.SnapshotAndReplace);
    }
}
