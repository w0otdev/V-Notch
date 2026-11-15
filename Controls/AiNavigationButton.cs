using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

// Only the small ink mark responds to navigation; the label and page stay still.
public sealed class AiNavigationButton : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(AiNavigationButton), new PropertyMetadata(false));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private FrameworkElement? _hover;
    private FrameworkElement? _press;
    private FrameworkElement? _mark;

    public AiNavigationButton()
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
        _hover = GetTemplateChild("HoverOverlay") as FrameworkElement;
        _press = GetTemplateChild("PressedOverlay") as FrameworkElement;
        _mark = GetTemplateChild("InkMark") as FrameworkElement;
        UpdateInteraction(animate: false);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty ||
            e.Property == IsKeyboardFocusedProperty || e.Property == IsEnabledProperty ||
            e.Property == IsSelectedProperty)
            UpdateInteraction(animate: IsLoaded && IsVisible);
    }

    private void UpdateInteraction(bool animate)
    {
        bool active = IsLoaded && IsVisible && IsEnabled;
        bool hover = active && (IsMouseOver || IsKeyboardFocused);
        bool pressed = active && IsPressed;
        animate &= !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        Retarget(_hover, OpacityProperty, hover ? 1 : 0, animate);
        Retarget(_press, OpacityProperty, pressed ? 1 : 0, animate);
        Retarget(_mark, WidthProperty, pressed ? 8 : IsSelected ? 22 : hover ? 14 : 4, animate);
        Retarget(_mark, OpacityProperty, IsSelected ? 1 : hover ? 0.65 : 0.2, animate);
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
        Retarget(_hover, OpacityProperty, 0, false);
        Retarget(_press, OpacityProperty, 0, false);
        Retarget(_mark, WidthProperty, 4, false);
        Retarget(_mark, OpacityProperty, 0.2, false);
    }

    private static void Retarget(FrameworkElement? element, DependencyProperty property, double value, bool animate)
    {
        if (element == null) return;
        double from = (double)element.GetValue(property);
        element.BeginAnimation(property, null);
        element.SetValue(property, value);
        if (!animate || !double.IsFinite(from) || Math.Abs(from - value) < 0.001) return;
        var motion = new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(motion, AnimationConfig.TargetFps);
        element.BeginAnimation(property, motion, HandoffBehavior.SnapshotAndReplace);
    }
}
