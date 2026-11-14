using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch.Controls;

// Motion stays local to the quota surfaces; hidden/unloaded views own no running clocks.
internal sealed class AiUsageMotion
{
    private readonly FrameworkElement _owner;
    private readonly Dictionary<string, Border> _bars = new();
    private readonly Dictionary<string, double> _previous = new();
    private readonly List<Action> _stopTransient = [];
    private bool _subscribed;
    private bool _reveal;
    private static readonly DependencyProperty BarProgressProperty = DependencyProperty.RegisterAttached(
        "BarProgress", typeof(double), typeof(AiUsageMotion), new PropertyMetadata(0.0));
    private static readonly DependencyProperty CounterValueProperty = DependencyProperty.RegisterAttached(
        "CounterValue", typeof(double), typeof(AiUsageMotion), new PropertyMetadata(0.0, OnCounterValueChanged));
    private static readonly DependencyProperty CounterFormatProperty = DependencyProperty.RegisterAttached(
        "CounterFormat", typeof(Func<double, string>), typeof(AiUsageMotion), new PropertyMetadata(null));
    private static readonly KeySpline SpringSpline = CreateSpline(0.18, 1.24, 0.36, 1.0);
    private static readonly KeySpline SnappySpline = CreateSpline(0.16, 1.0, 0.30, 1.0);
    private static readonly BarWidthConverter WidthConverter = new();

    private static KeySpline CreateSpline(double x1, double y1, double x2, double y2)
    {
        var spline = new KeySpline(x1, y1, x2, y2);
        spline.Freeze();
        return spline;
    }

    private static void OnCounterValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d.GetValue(CounterFormatProperty) is not Func<double, string> formatter) return;
        string text = formatter((double)e.NewValue);
        if (d is Run run) run.Text = text;
        else if (d is TextBlock tb) tb.Text = text;
    }

    internal static bool MotionEnabled => AiUsageAppearance.Current.MotionIntensity > 0 && !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static double Strength => AiUsageAppearance.Current.MotionIntensity / 100.0;
    private bool CanAnimate => MotionEnabled && _owner.IsLoaded && _owner.IsVisible &&
        _owner is not AiUsageNotchView { IsPreview: true };
    internal bool CanAnimateNow => CanAnimate;

    internal AiUsageMotion(FrameworkElement owner)
    {
        _owner = owner;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
        owner.IsVisibleChanged += (_, _) => { if (!owner.IsVisible) Stop(); };
        if (owner.IsLoaded) OnLoaded(owner, new RoutedEventArgs());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        AnimationConfig.ReduceMotionChanged += OnMotionChanged;
        AiUsageAppearance.Changed += OnMotionChanged;
        SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _subscribed = false;
        AnimationConfig.ReduceMotionChanged -= OnMotionChanged;
        AiUsageAppearance.Changed -= OnMotionChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
        Stop();
        _bars.Clear();
        _previous.Clear();
    }

    private void OnSystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) OnMotionChanged();
    }

    private void OnMotionChanged()
    {
        if (!_owner.Dispatcher.CheckAccess()) { _owner.Dispatcher.BeginInvoke(new Action(OnMotionChanged)); return; }
        if (!MotionEnabled) Stop();
    }

    internal void BeginPresentation(bool reveal)
    {
        _previous.Clear();
        foreach (var (key, fill) in _bars)
        {
            _previous[key] = (double)fill.GetValue(BarProgressProperty);
            fill.BeginAnimation(BarProgressProperty, null);
        }
        _bars.Clear();
        foreach (var stop in _stopTransient) stop();
        _stopTransient.Clear();
        _reveal = reveal;
    }

    internal void BindBar(Border fill, string key, double remaining, bool available)
    {
        double target = available ? Math.Clamp(remaining / 100, 0, 1) : 0;
        // Resize the actual pill. Scaling its geometry also squashes the end caps.
        fill.HorizontalAlignment = HorizontalAlignment.Left;
        fill.SetValue(BarProgressProperty, target);
        var width = new MultiBinding { Converter = WidthConverter };
        width.Bindings.Add(new Binding(nameof(FrameworkElement.ActualWidth))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(FrameworkElement), 1)
        });
        width.Bindings.Add(new Binding { Source = fill, Path = new PropertyPath(BarProgressProperty) });
        fill.SetBinding(FrameworkElement.WidthProperty, width);
        if (available) _bars[key] = fill;
        double from = _reveal ? 1 : _previous.TryGetValue(key, out double previous) ? previous : (_owner is AiUsageStatWidget { IsPreview: true } ? target : 1);
        if (!CanAnimate || !available || Math.Abs(from - target) < 0.001) return;
        int scaled = ScaleDuration(300);
        fill.BeginAnimation(BarProgressProperty, SplineTween(from, target, scaled, SnappySpline));
        AttachBarFlash(fill, scaled, 0.24);
    }

    internal void PulseBar(Border fill, double? fromProgress = null, int durationMs = 300, int delayMs = 0)
    {
        if (!CanAnimate) return;
        int scaled = ScaleDuration(durationMs);
        double target = (double)fill.GetValue(BarProgressProperty);
        if (target <= 0.001) return;
        if (fromProgress is { } from && Math.Abs(from - target) > 0.005)
            fill.BeginAnimation(BarProgressProperty, SplineTween(Math.Clamp(from, 0, 1), target, scaled, SnappySpline, delayMs));
        AttachBarFlash(fill, scaled, 0.30, delayMs);
    }

    internal void AnimateSpringTransform(FrameworkElement element, double fromX, double fromY,
        double fromScale = 1.0, double fromOpacity = 1.0, double fromAngle = 0.0,
        int durationMs = 300, int delayMs = 0, bool scaleFromTop = false, double fromScaleY = 1.0,
        bool scaleFlipDelta = false)
    {
        if (!CanAnimate) return;
        int scaled = ScaleDuration(durationMs);
        double motionScale = 0.35 + 0.65 * Strength;
        if (scaleFlipDelta)
        {
            fromX *= motionScale;
            fromY *= motionScale;
        }
        fromScale = 1.0 + (fromScale - 1.0) * motionScale;
        fromScaleY = 1.0 + (fromScaleY - 1.0) * motionScale;
        fromAngle *= motionScale;

        var scale = new ScaleTransform(1.0, 1.0);
        var rotate = new RotateTransform(0.0);
        var translate = new TranslateTransform(0.0, 0.0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);
        element.RenderTransformOrigin = scaleFromTop ? new Point(0.5, 0.0) : new Point(0.5, 0.5);
        element.RenderTransform = group;

        var activeSpline = Strength >= 0.3 ? SpringSpline : SnappySpline;
        if (Math.Abs(fromX) > 0.1)
            translate.BeginAnimation(TranslateTransform.XProperty, SplineTween(fromX, 0, scaled, activeSpline, delayMs));
        if (Math.Abs(fromY) > 0.1)
            translate.BeginAnimation(TranslateTransform.YProperty, SplineTween(fromY, 0, scaled, activeSpline, delayMs));
        if (Math.Abs(fromScale - 1.0) > 0.005)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, SplineTween(fromScale, 1.0, scaled, activeSpline, delayMs));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, SplineTween(fromScale, 1.0, scaled, activeSpline, delayMs));
        }
        else if (Math.Abs(fromScaleY - 1.0) > 0.005)
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, SplineTween(Math.Clamp(fromScaleY, 0.3, 2.6), 1.0, scaled, activeSpline, delayMs));
        }
        if (Math.Abs(fromAngle) > 0.1)
            rotate.BeginAnimation(RotateTransform.AngleProperty, SplineTween(fromAngle, 0, scaled, activeSpline, delayMs));
        if (fromOpacity < 0.99)
        {
            double targetOpacity = element.Opacity;
            element.BeginAnimation(UIElement.OpacityProperty,
                SplineTween(fromOpacity * targetOpacity, targetOpacity, Math.Max(120, scaled - 40), SnappySpline, delayMs));
        }

        _stopTransient.Add(() =>
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
        });
    }

    internal void AnimateColorBrush(Border target, Color fromColor, Color toColor, Action restoreBinding, int durationMs = 320)
    {
        if (!CanAnimate || fromColor == toColor) return;
        bool canceled = false;
        var brush = new SolidColorBrush(toColor);
        target.Background = brush;
        var anim = new ColorAnimation(fromColor, toColor, TimeSpan.FromMilliseconds(ScaleDuration(durationMs)))
        {
            EasingFunction = AnimationPrimitives._easeAppleOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        anim.Completed += (_, _) =>
        {
            if (!canceled) restoreBinding();
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        _stopTransient.Add(() =>
        {
            canceled = true;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            restoreBinding();
        });
    }

    internal void AnimateNumber(DependencyObject target, double fromValue, double toValue,
        Func<double, string> formatter, string finalText, int durationMs = 320, int delayMs = 0)
    {
        if (!CanAnimate || Math.Abs(fromValue - toValue) < 0.05 || target is not IAnimatable animatable) return;
        target.SetValue(CounterFormatProperty, formatter);
        target.SetValue(CounterValueProperty, toValue);
        var anim = SplineTween(fromValue, toValue, ScaleDuration(durationMs), SnappySpline, delayMs);
        anim.Completed += (_, _) => SetTextTarget(target, finalText);
        animatable.BeginAnimation(CounterValueProperty, anim, HandoffBehavior.SnapshotAndReplace);
        _stopTransient.Add(() =>
        {
            animatable.BeginAnimation(CounterValueProperty, null);
            SetTextTarget(target, finalText);
        });
    }

    private static void SetTextTarget(DependencyObject target, string text)
    {
        if (target is Run run) run.Text = text;
        else if (target is TextBlock tb) tb.Text = text;
    }

    private void AttachBarFlash(Border fill, int durationMs, double peakOpacity, int delayMs = 0)
    {
        // One solid highlight marks a quota update; there is no idle animation.
        var light = new Border { Background = Brushes.White, CornerRadius = fill.CornerRadius, Opacity = 0, IsHitTestVisible = false };
        fill.Child = light;
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(durationMs), FillBehavior = FillBehavior.Stop };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(peakOpacity, KeyTime.FromPercent(0.32)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        if (delayMs > 0) flash.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        Timeline.SetDesiredFrameRate(flash, AnimationConfig.TargetFps);
        light.BeginAnimation(UIElement.OpacityProperty, flash);
        _stopTransient.Add(() => light.BeginAnimation(UIElement.OpacityProperty, null));
    }

    private static DoubleAnimationUsingKeyFrames SplineTween(double from, double to, int milliseconds, KeySpline spline, int delayMs = 0)
    {
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), spline));
        if (delayMs > 0) anim.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        return anim;
    }

    private static int ScaleDuration(int baseMs) =>
        Math.Clamp((int)Math.Round(baseMs * (0.65 + 0.35 * Strength)), 90, baseMs);

    internal void Reveal(FrameworkElement element, int order)
    {
        if (!_reveal || !CanAnimate) return;
        double distance = 3 + 7 * Strength;
        var offset = new TranslateTransform(0, distance);
        element.RenderTransform = offset;
        var movement = Tween(distance, 0, 220);
        var fade = Tween(0, element.Opacity, 180);
        movement.BeginTime = fade.BeginTime = TimeSpan.FromMilliseconds(order * 35);
        offset.Y = 0;
        offset.BeginAnimation(TranslateTransform.YProperty, movement);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        _stopTransient.Add(() => { offset.BeginAnimation(TranslateTransform.YProperty, null); element.BeginAnimation(UIElement.OpacityProperty, null); });
    }

    internal IDisposable? Busy(FrameworkElement? source)
    {
        if (!CanAnimate || source is not Border { Child: FrameworkElement icon }) return null;
        // Text actions pulse while pending; the refresh glyph rotates about its center.
        Action stop;
        if (icon is System.Windows.Shapes.Path)
        {
            var rotation = new RotateTransform();
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = rotation;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, AnimationConfig.TargetFps);
            rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
            stop = () => rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
        else
        {
            var pulse = new DoubleAnimation(1, 0.5, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(pulse, AnimationConfig.TargetFps);
            icon.BeginAnimation(UIElement.OpacityProperty, pulse);
            stop = () => icon.BeginAnimation(UIElement.OpacityProperty, null);
        }
        _stopTransient.Add(stop);
        return new BusyLease(() => { stop(); _stopTransient.Remove(stop); });
    }

    private static DoubleAnimation Tween(double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = AnimationPrimitives._easeAppleOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        return animation;
    }

    private void Stop()
    {
        foreach (var fill in _bars.Values) fill.BeginAnimation(BarProgressProperty, null);
        foreach (var stop in _stopTransient) stop();
        _stopTransient.Clear();
    }

    private sealed class BusyLease(Action stop) : IDisposable
    {
        private Action? _stop = stop;
        public void Dispose() => Interlocked.Exchange(ref _stop, null)?.Invoke();
    }

    private sealed class BarWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            values is [double width, double progress] && double.IsFinite(width) && double.IsFinite(progress)
                ? Math.Max(0, width) * Math.Clamp(progress, 0, 1) : 0.0;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
