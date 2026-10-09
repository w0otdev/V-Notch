using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public partial class DotLoadingIndicator : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DotLoadingIndicator),
        new PropertyMetadata(true, (owner, _) => ((DotLoadingIndicator)owner).RefreshMotion()));

    private static readonly double[] Sizes = [.78, .93, 1, .82, .55, .62];
    private static readonly double[] Heights = [.94, .78, .92, 1, .72, .58];
    private static readonly double[] Brightness = [.76, .9, 1, .96, .62, .7];
    private static readonly double[] GlowBrightness = [.45, .7, 1, .8, .5, .35];
    private static readonly double[] GlowSizes = [.82, 1, 1.12, 1.04, .9, .8];
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(6);
    private readonly Storyboard _motion = new() { RepeatBehavior = RepeatBehavior.Forever };

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    internal bool IsAnimating { get; private set; }

    public DotLoadingIndicator()
    {
        InitializeComponent();
        var dots = Dots.Children.Cast<Grid>().ToArray();
        for (int i = 0; i < dots.Length; i++)
        {
            var scale = new ScaleTransform(Sizes[i], Heights[i]);
            dots[i].RenderTransform = scale;
            dots[i].Opacity = Brightness[i];
            AddWave(dots[i], new PropertyPath("(0).(1)", RenderTransformProperty, ScaleTransform.ScaleXProperty), Sizes, i);
            AddWave(dots[i], new PropertyPath("(0).(1)", RenderTransformProperty, ScaleTransform.ScaleYProperty), Heights, i);
            AddWave(dots[i], new PropertyPath(OpacityProperty), Brightness, i);
            var glow = (System.Windows.Shapes.Ellipse)dots[i].Children[0];
            glow.RenderTransformOrigin = new Point(.5, .5);
            glow.RenderTransform = new ScaleTransform(GlowSizes[i], GlowSizes[i]);
            glow.Opacity = GlowBrightness[i];
            AddWave(glow, new PropertyPath(OpacityProperty), GlowBrightness, i);
            AddWave(glow, new PropertyPath("(0).(1)", RenderTransformProperty, ScaleTransform.ScaleXProperty), GlowSizes, i);
            AddWave(glow, new PropertyPath("(0).(1)", RenderTransformProperty, ScaleTransform.ScaleYProperty), GlowSizes, i);
        }
        Dots.RenderTransform = new RotateTransform(0, 24, 24);
        var rotation = new DoubleAnimation(0, 360, Cycle);
        Storyboard.SetTarget(rotation, Dots);
        Storyboard.SetTargetProperty(rotation, new PropertyPath("(0).(1)", RenderTransformProperty, RotateTransform.AngleProperty));
        _motion.Children.Add(rotation);
        Loaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged += RefreshMotion;
            RefreshMotion();
        };
        Unloaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= RefreshMotion;
            StopMotion();
        };
        IsVisibleChanged += (_, _) => RefreshMotion();
    }

    private void AddWave(DependencyObject target, PropertyPath property, double[] values, int phase)
    {
        // A single shared clock advances the size/brightness wave clockwise.
        // Only render properties change; the indicator never remeasures its dots.
        var wave = new DoubleAnimationUsingKeyFrames { Duration = Cycle };
        for (int step = 0; step <= values.Length; step++)
            wave.KeyFrames.Add(new EasingDoubleKeyFrame(values[(phase - step + values.Length) % values.Length],
                KeyTime.FromTimeSpan(TimeSpan.FromTicks(Cycle.Ticks * step / values.Length)), AnimationPrimitives._easeSineInOut));
        Storyboard.SetTarget(wave, target);
        Storyboard.SetTargetProperty(wave, property);
        _motion.Children.Add(wave);
    }

    private void RefreshMotion()
    {
        bool animate = IsActive && IsLoaded && IsVisible && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        if (animate == IsAnimating) return;
        if (!animate) { StopMotion(); return; }
        Timeline.SetDesiredFrameRate(_motion, Math.Min(60, AnimationConfig.TargetFps));
        _motion.Begin(this, isControllable: true);
        IsAnimating = true;
    }

    private void StopMotion()
    {
        if (!IsAnimating) return;
        _motion.Remove(this);
        IsAnimating = false;
    }
}
