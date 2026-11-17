using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>A readable foreground highlight for busy labels, with no layout animation.</summary>
public static class TextShimmer
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(TextShimmer), new PropertyMetadata(false, OnActiveChanged));
    private static readonly DependencyProperty LifetimeProperty = DependencyProperty.RegisterAttached(
        "Lifetime", typeof(Lifetime), typeof(TextShimmer));

    public static bool GetIsActive(DependencyObject owner) => (bool)owner.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject owner, bool value) => owner.SetValue(IsActiveProperty, value);

    private static void OnActiveChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not TextBlock text) return;
        if ((bool)args.NewValue)
        {
            var lifetime = new Lifetime(text);
            text.SetValue(LifetimeProperty, lifetime);
            lifetime.Attach();
        }
        else if (text.GetValue(LifetimeProperty) is Lifetime lifetime)
        {
            lifetime.Detach();
            text.ClearValue(LifetimeProperty);
        }
    }

    private sealed class Lifetime(TextBlock text)
    {
        private Brush? _original;
        private Brush? _originalMask;
        private SolidColorBrush? _ink;
        private LinearGradientBrush? _highlight;
        private TranslateTransform? _sweep;

        internal void Attach()
        {
            text.Loaded += OnLoaded;
            text.Unloaded += OnUnloaded;
            text.IsVisibleChanged += OnVisibleChanged;
            if (text.IsLoaded) OnLoaded(text, new RoutedEventArgs());
        }

        internal void Detach()
        {
            text.Loaded -= OnLoaded;
            text.Unloaded -= OnUnloaded;
            text.IsVisibleChanged -= OnVisibleChanged;
            OnUnloaded(text, new RoutedEventArgs());
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            AnimationConfig.ReduceMotionChanged -= Refresh;
            AnimationConfig.ReduceMotionChanged += Refresh;
            SystemParameters.StaticPropertyChanged -= OnSystemChanged;
            SystemParameters.StaticPropertyChanged += OnSystemChanged;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            AnimationConfig.ReduceMotionChanged -= Refresh;
            SystemParameters.StaticPropertyChanged -= OnSystemChanged;
            Stop();
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
        private void OnSystemChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) Refresh();
        }

        private void Refresh()
        {
            if (!text.Dispatcher.CheckAccess()) { text.Dispatcher.BeginInvoke(new Action(Refresh)); return; }
            bool animate = GetIsActive(text) && text.IsLoaded && text.IsVisible &&
                !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation && !SystemParameters.HighContrast;
            if (!animate) { Stop(); return; }
            if (_highlight != null) return;

            _original = text.Foreground;
            _originalMask = text.OpacityMask;
            Color baseline = _original is SolidColorBrush solid ? solid.Color : Color.FromRgb(150, 155, 164);
            byte baseAlpha = (byte)Math.Clamp((int)Math.Max(baseline.R, Math.Max(baseline.G, baseline.B)), 140, 210);
            _ink = new SolidColorBrush(Color.FromArgb(baseline.A, 245, 247, 250));
            _ink.Freeze();
            // The first frame must match the start of the clock, before WPF ticks.
            _sweep = new TranslateTransform(-1, 0);
            _highlight = new LinearGradientBrush
            {
                StartPoint = new Point(0, .5),
                EndPoint = new Point(1, .5),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                RelativeTransform = _sweep,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(baseAlpha, 255, 255, 255), 0),
                    new(Color.FromArgb(baseAlpha, 255, 255, 255), .15),
                    new(Colors.White, .5),
                    new(Color.FromArgb(baseAlpha, 255, 255, 255), .85),
                    new(Color.FromArgb(baseAlpha, 255, 255, 255), 1)
                }
            };
            // Mask the complete label once. A foreground gradient is independently
            // mapped by glyph runs and cached digit reels, fragmenting the sweep.
            text.SetCurrentValue(TextBlock.ForegroundProperty, _ink);
            text.SetCurrentValue(UIElement.OpacityMaskProperty, _highlight);
            // Match the existing lyric-search shimmer's 1600 ms linear sweep.
            // Keep stops within [0, 1]: WPF pads from the gradient endpoints.
            // Equal alpha at both ends makes the off-label loop reset invisible.
            var animation = new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1600))
            { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(animation, Math.Min(60, AnimationConfig.TargetFps));
            _sweep.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void Stop()
        {
            if (_highlight == null) return;
            _sweep?.BeginAnimation(TranslateTransform.XProperty, null);
            if (ReferenceEquals(text.Foreground, _ink))
                text.SetCurrentValue(TextBlock.ForegroundProperty, _original);
            if (ReferenceEquals(text.OpacityMask, _highlight))
                text.SetCurrentValue(UIElement.OpacityMaskProperty, _originalMask);
            _highlight = null;
            _ink = null;
            _sweep = null;
            _original = null;
            _originalMask = null;
        }
    }
}
