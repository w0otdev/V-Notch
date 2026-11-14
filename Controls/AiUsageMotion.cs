using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private static readonly BarWidthConverter WidthConverter = new();

    internal static bool MotionEnabled => AiUsageAppearance.Current.MotionIntensity > 0 && !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static double Strength => AiUsageAppearance.Current.MotionIntensity / 100.0;
    private bool CanAnimate => MotionEnabled && _owner.IsLoaded && _owner.IsVisible &&
        _owner is not AiUsageStatWidget { IsPreview: true } && _owner is not AiUsageNotchView { IsPreview: true };

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
        double from = _reveal ? 1 : _previous.TryGetValue(key, out double previous) ? previous : 1;
        if (!CanAnimate || !available || Math.Abs(from - target) < 0.001) return;
        fill.BeginAnimation(BarProgressProperty, Tween(from, target, 280));

        // One solid highlight marks a quota update; there is no idle animation.
        var light = new Border { Background = Brushes.White, CornerRadius = fill.CornerRadius, Opacity = 0, IsHitTestVisible = false };
        fill.Child = light;
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(280), FillBehavior = FillBehavior.Stop };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.2, KeyTime.FromPercent(0.35)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(flash, AnimationConfig.TargetFps);
        light.BeginAnimation(UIElement.OpacityProperty, flash);
        _stopTransient.Add(() => light.BeginAnimation(UIElement.OpacityProperty, null));
    }

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
