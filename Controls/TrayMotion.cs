using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controls;

public sealed class TrayCopiedOpacityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? 1d : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class TrayCopiedBlurConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? 16d : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

// Animate presentation brushes without replacing the bindings that own state.
public static class TrayMotion
{
    internal static EasingFunctionBase EaseOut { get; } = CreateEaseOut();

    private static EasingFunctionBase CreateEaseOut()
    {
        // The spline already describes ease-out; use its progress directly.
        var curve = new EaseOutCurve { EasingMode = EasingMode.EaseIn };
        curve.Freeze();
        return curve;
    }

    private sealed class EaseOutCurve : EasingFunctionBase
    {
        private static readonly KeySpline Curve = CreateCurve();
        private static KeySpline CreateCurve()
        {
            var curve = new KeySpline(0.23, 1, 0.32, 1);
            curve.Freeze();
            return curve;
        }
        protected override double EaseInCore(double normalizedTime) => Curve.GetSplineProgress(normalizedTime);
        protected override Freezable CreateInstanceCore() => new EaseOutCurve();
    }

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.RegisterAttached(
        "Background", typeof(Brush), typeof(TrayMotion), new PropertyMetadata(null, BrushChanged));
    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.RegisterAttached(
        "BorderBrush", typeof(Brush), typeof(TrayMotion), new PropertyMetadata(null, BrushChanged));
    public static readonly DependencyProperty OpacityProperty = DependencyProperty.RegisterAttached(
        "Opacity", typeof(double), typeof(TrayMotion), new PropertyMetadata(1d, OpacityChanged));
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.RegisterAttached(
        "BlurRadius", typeof(double), typeof(TrayMotion), new PropertyMetadata(0d, BlurRadiusChanged));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.RegisterAttached(
        "Angle", typeof(double), typeof(TrayMotion), new PropertyMetadata(0d, AngleChanged));

    public static Brush GetBackground(DependencyObject element) => (Brush)element.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject element, Brush value) => element.SetValue(BackgroundProperty, value);
    public static Brush GetBorderBrush(DependencyObject element) => (Brush)element.GetValue(BorderBrushProperty);
    public static void SetBorderBrush(DependencyObject element, Brush value) => element.SetValue(BorderBrushProperty, value);
    public static double GetOpacity(DependencyObject element) => (double)element.GetValue(OpacityProperty);
    public static void SetOpacity(DependencyObject element, double value) => element.SetValue(OpacityProperty, value);
    public static double GetBlurRadius(DependencyObject element) => (double)element.GetValue(BlurRadiusProperty);
    public static void SetBlurRadius(DependencyObject element, double value) => element.SetValue(BlurRadiusProperty, value);
    public static double GetAngle(DependencyObject element) => (double)element.GetValue(AngleProperty);
    public static void SetAngle(DependencyObject element, double value) => element.SetValue(AngleProperty, value);

    private static void AngleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement { RenderTransform: TransformGroup transforms } ||
            transforms.Children.OfType<RotateTransform>().FirstOrDefault() is not { } rotation) return;
        double current = rotation.Angle;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        rotation.Angle = (double)args.NewValue;
        if (AnimationConfig.ReduceMotion) return;
        var animation = new DoubleAnimation(current, rotation.Angle, TimeSpan.FromMilliseconds(180))
        { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private static void BrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Border border) return;
        var property = args.Property == BackgroundProperty ? Border.BackgroundProperty : Border.BorderBrushProperty;
        var previous = border.GetValue(property) as SolidColorBrush;
        if (args.NewValue is not SolidColorBrush target || previous == null || !border.IsLoaded)
        { border.SetValue(property, args.NewValue); return; }
        var brush = new SolidColorBrush(target.Color);
        border.SetValue(property, brush);
        var animation = new ColorAnimation(previous.Color, target.Color, TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : 180))
        { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static void OpacityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        double current = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = (double)args.NewValue;
        if (!element.IsLoaded) return;
        var animation = new DoubleAnimation(current, element.Opacity, TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : 140))
        { FillBehavior = FillBehavior.Stop, EasingFunction = EaseOut };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void BlurRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        double target = (double)args.NewValue;

        if (target <= 0.001)
        {
            if (element.Effect is BlurEffect existing)
            {
                double current = existing.Radius;
                existing.BeginAnimation(BlurEffect.RadiusProperty, null);
                existing.Radius = 0d;

                if (!element.IsLoaded || AnimationConfig.ReduceMotion)
                {
                    element.ClearValue(UIElement.EffectProperty);
                    return;
                }

                var animation = new DoubleAnimation(current, 0d, TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : 160))
                { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
                animation.Completed += (s, e) =>
                {
                    if (GetBlurRadius(element) <= 0.001)
                    {
                        if (element.Effect is BlurEffect be) be.BeginAnimation(BlurEffect.RadiusProperty, null);
                        element.ClearValue(UIElement.EffectProperty);
                    }
                };
                existing.BeginAnimation(BlurEffect.RadiusProperty, animation);
            }
            else
            {
                element.ClearValue(UIElement.EffectProperty);
            }
            return;
        }

        BlurEffect blur;
        if (element.Effect is BlurEffect currentBlur)
        {
            blur = currentBlur;
        }
        else
        {
            blur = new BlurEffect { Radius = 0d, RenderingBias = RenderingBias.Performance };
            element.Effect = blur;
        }

        double currentRadius = blur.Radius;
        blur.BeginAnimation(BlurEffect.RadiusProperty, null);
        blur.Radius = target;

        if (!element.IsLoaded || AnimationConfig.ReduceMotion) return;

        var anim = new DoubleAnimation(currentRadius, target, TimeSpan.FromMilliseconds(AnimationConfig.ReduceMotion ? 80 : 180))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
    }
}
