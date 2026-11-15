using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controls;

internal sealed partial class AiUsageMotion
{
    private static readonly KeySpline ViewEase = CreateSpline(0.22, 0.72, 0.22, 1);

    // A short opening beat lets the shell expand, then the two accounts unfold
    // from the centre. Rows and meters follow through on separate timelines.
    private static int ViewDelay(int milliseconds) => (int)Math.Round(milliseconds * (0.65 + 0.35 * Strength));

    internal void RevealViewPane(FrameworkElement pane, int index, int count)
    {
        if (!_reveal || !CanAnimate) return;
        double inward = count > 1 ? (index == 0 ? 1 : -1) * (8 + 12 * Strength) : 0;
        double scale = 1 - 0.04 * Strength;
        RevealViewLayer(pane, inward, 10 + 10 * Strength, scale,
            scale, ViewDelay(40 + index * 65), ScaleDuration(560),
            fade: false, blurRadius: 3 + 7 * Strength);
    }

    internal void RevealViewSection(FrameworkElement element, int providerIndex, int sectionIndex)
    {
        if (!_reveal || !CanAnimate) return;
        int delay = ViewDelay(70 + providerIndex * 65 + sectionIndex * 55);
        double distance = sectionIndex == 0 ? 0 : 8 + 8 * Strength;
        RevealViewLayer(element, 0, distance, 1, 1, delay, ScaleDuration(440));

        foreach (var fill in _bars.Values)
        {
            if (element.IsAncestorOf(fill)) RevealViewMeter(fill, delay + ViewDelay(65));
        }
    }

    internal void RevealViewDivider(FrameworkElement divider)
    {
        if (!_reveal || !CanAnimate) return;
        RevealViewLayer(divider, 0, 0, 1, 0, ViewDelay(100), ScaleDuration(540));
    }

    private void RevealViewLayer(FrameworkElement element, double x, double y, double scaleX,
        double scaleY, int delay, int duration, bool fade = true, double blurRadius = 0)
    {
        var originalTransform = element.RenderTransform;
        var originalOrigin = element.RenderTransformOrigin;
        var originalEffect = element.Effect;
        var scale = new ScaleTransform(1, 1);
        var offset = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(originalTransform);
        transform.Children.Add(scale);
        transform.Children.Add(offset);
        element.RenderTransformOrigin = new Point(0.5, 0);
        element.RenderTransform = transform;

        // Only the two account panes use blur, and only during their arrival.
        BlurEffect? blur = blurRadius > 0 && originalEffect == null
            ? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance } : null;
        if (blur != null)
        {
            element.Effect = blur;
            var focus = ViewTween(blurRadius, 0, delay, ScaleDuration(300));
            focus.Completed += (_, _) => ClearBlur();
            blur.BeginAnimation(BlurEffect.RadiusProperty, focus);
        }

        void ClearBlur()
        {
            blur?.BeginAnimation(BlurEffect.RadiusProperty, null);
            if (blur != null && ReferenceEquals(element.Effect, blur)) element.Effect = originalEffect;
        }

        void Settle()
        {
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (fade) element.BeginAnimation(UIElement.OpacityProperty, null);
            ClearBlur();
            if (ReferenceEquals(element.RenderTransform, transform))
            {
                element.RenderTransform = originalTransform;
                element.RenderTransformOrigin = originalOrigin;
            }
        }

        _stopTransient.Add(Settle);
        var movement = ViewTween(y, 0, delay, duration);
        movement.Completed += (_, _) => { Settle(); _stopTransient.Remove(Settle); };
        offset.BeginAnimation(TranslateTransform.XProperty, ViewTween(x, 0, delay, duration));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, ViewTween(scaleX, 1, delay, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, ViewTween(scaleY, 1, delay, duration));
        if (fade)
            element.BeginAnimation(UIElement.OpacityProperty, ViewTween(0, element.Opacity, delay, ScaleDuration(280)));
        offset.BeginAnimation(TranslateTransform.YProperty, movement);
    }

    private void RevealViewMeter(Border fill, int delay)
    {
        double target = (double)fill.GetAnimationBaseValue(BarProgressProperty);
        if (target <= 0) return;
        var arrival = ViewTween(0, target, delay, ScaleDuration(520));
        arrival.Completed += (_, _) => fill.BeginAnimation(BarProgressProperty, null);
        fill.BeginAnimation(BarProgressProperty, arrival);

        var originalChild = fill.Child;
        var sweep = new TranslateTransform(-1, 0);
        var light = new Border
        {
            CornerRadius = fill.CornerRadius, Opacity = 0, IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = sweep,
                GradientStops = new GradientStopCollection
                {
                    new(Colors.Transparent, 0), new(Colors.Transparent, 0.25),
                    new(Colors.White, 0.55), new(Colors.Transparent, 0.85), new(Colors.Transparent, 1)
                }
            }
        };
        fill.Child = light;
        void ClearSweep()
        {
            sweep.BeginAnimation(TranslateTransform.XProperty, null);
            light.BeginAnimation(UIElement.OpacityProperty, null);
            if (ReferenceEquals(fill.Child, light)) fill.Child = originalChild;
        }
        _stopTransient.Add(ClearSweep);
        int sweepDuration = ScaleDuration(600);
        var travel = ViewTween(-1, 1, delay, sweepDuration);
        travel.Completed += (_, _) => { ClearSweep(); _stopTransient.Remove(ClearSweep); };
        var flash = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.5 * Strength, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + sweepDuration * 0.4))));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + sweepDuration))));
        Timeline.SetDesiredFrameRate(flash, Math.Min(90, AnimationConfig.TargetFps));
        light.BeginAnimation(UIElement.OpacityProperty, flash);
        sweep.BeginAnimation(TranslateTransform.XProperty, travel);
    }

    private static DoubleAnimationUsingKeyFrames ViewTween(double from, double to, int delay, int duration)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        // Hold the initial frame during the stagger, including before the first render.
        if (delay > 0)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)), ViewEase));
        Timeline.SetDesiredFrameRate(animation, Math.Min(90, AnimationConfig.TargetFps));
        return animation;
    }
}
