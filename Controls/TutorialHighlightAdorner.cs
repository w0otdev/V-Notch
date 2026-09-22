using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>A lightweight, full-surface focus on the real control. Input always passes through.</summary>
internal sealed class TutorialHighlightAdorner : Adorner
{
    private static readonly Pen[] HaloPens = CreateHaloPens();
    private static readonly Pen CorePen = CreateCorePen();
    private static readonly Pen OutlinePen = CreateOutlinePen();
    private static readonly Brush SurfaceBrush = CreateSurfaceBrush();
    private readonly DrawingGroup _surface = new();
    private readonly DrawingGroup _halo = new();
    private readonly DrawingGroup _core = new();
    private Rect _drawnBounds = Rect.Empty;
    private double _drawnRadius = double.NaN;
    private bool _breathing;

    internal bool IsBreathing => _breathing;

    public TutorialHighlightAdorner(UIElement target) : base(target)
    {
        IsHitTestVisible = false;
        IsEnabled = false;
        _surface.Opacity = .68;
        _halo.Opacity = .76;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static Brush CreateSurfaceBrush()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(25, 105, 183, 255),
            Color.FromArgb(10, 153, 126, 255), 25);
        brush.Freeze();
        return brush;
    }

    private static Pen[] CreateHaloPens()
    {
        return new[] { (10d, (byte)35), (6d, (byte)68), (3d, (byte)104) }
            .Select(layer =>
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(layer.Item2, 126, 183, 255)), layer.Item1);
                pen.Freeze();
                return pen;
            }).ToArray();
    }

    private static Pen CreateCorePen()
    {
        var brush = new LinearGradientBrush();
        brush.StartPoint = new Point(0, 0);
        brush.EndPoint = new Point(1, 1);
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(238, 247, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(147, 199, 255), .45));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(209, 224, 255), 1));
        var pen = new Pen(brush, 1.5);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateOutlinePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(100, 147, 199, 255)), 0.8);
        pen.Freeze();
        return pen;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
        if (!CanAnimate) return;
        var entrance = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(entrance, Math.Min(AnimationConfig.TargetFps, 60));
        BeginAnimation(OpacityProperty, entrance);
        StartBreathing();
    }

    private bool CanAnimate => IsLoaded && SystemParameters.ClientAreaAnimation && !AnimationConfig.ReduceMotion;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
        StopAnimation();
    }

    private void OnReduceMotionChanged()
    {
        if (AnimationConfig.ReduceMotion) StopAnimation();
        else if (CanAnimate) StartBreathing();
    }

    private void StartBreathing()
    {
        if (_breathing || !CanAnimate) return;
        _breathing = true;
        // Animate retained drawing opacity only: no layout, geometry rebuild or effects per frame.
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        void Pulse(DrawingGroup layer, double low, double high, int duration)
        {
            var animation = new DoubleAnimation(low, high, TimeSpan.FromMilliseconds(duration))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease
            };
            Timeline.SetDesiredFrameRate(animation, Math.Min(AnimationConfig.TargetFps, 60));
            layer.BeginAnimation(DrawingGroup.OpacityProperty, animation);
        }
        Pulse(_halo, .53, 1, 1450);
        Pulse(_surface, .48, .95, 1450);
    }

    private void StopBreathing()
    {
        _halo.BeginAnimation(DrawingGroup.OpacityProperty, null);
        _surface.BeginAnimation(DrawingGroup.OpacityProperty, null);
        _breathing = false;
    }

    internal void FadeOut(Action remove)
    {
        StopBreathing();
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation || AnimationConfig.ReduceMotion)
        {
            StopAnimation();
            remove();
            return;
        }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = AnimationPrimitives._easeAppleOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(fade, Math.Min(AnimationConfig.TargetFps, 60));
        fade.Completed += (_, _) => { StopAnimation(); remove(); };
        BeginAnimation(OpacityProperty, fade);
    }

    internal void StopAnimation()
    {
        StopBreathing();
        BeginAnimation(OpacityProperty, null);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(AdornedElement.RenderSize);
        if (bounds.Width <= 12 || bounds.Height <= 12) return;
        // Keep the halo within the adorner bounds even when the target clips children.
        bounds.Inflate(-5.5, -5.5);
        double radius = AdornedElement is Border border
            ? Math.Max(border.CornerRadius.BottomLeft, border.CornerRadius.TopLeft)
            : Math.Min(10, bounds.Height / 2);
        radius = Math.Clamp(radius - 2, 0, Math.Min(bounds.Width, bounds.Height) / 2);
        // Cache the geometry. Layout changes redraw it once; rendering never drives a timer.
        if (bounds != _drawnBounds || radius != _drawnRadius)
        {
            _drawnBounds = bounds;
            _drawnRadius = radius;
            using (var surface = _surface.Open())
                surface.DrawRoundedRectangle(SurfaceBrush, null, bounds, radius, radius);
            using (var halo = _halo.Open())
                foreach (var pen in HaloPens)
                    halo.DrawRoundedRectangle(null, pen, bounds, radius, radius);
            using (var core = _core.Open())
            {
                core.DrawRoundedRectangle(null, OutlinePen, bounds, radius, radius);
                core.DrawRoundedRectangle(null, CorePen, bounds, radius, radius);
            }
        }
        drawingContext.DrawDrawing(_surface);
        drawingContext.DrawDrawing(_halo);
        drawingContext.DrawDrawing(_core);
    }
}
