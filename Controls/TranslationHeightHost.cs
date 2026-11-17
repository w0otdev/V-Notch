using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

// Keep text at its measured size while the space around it opens or closes.
// Animating the TextBox/ScrollViewer height itself repeatedly reflows its text.
public sealed class TranslationHeightHost : Decorator
{
    private static readonly DependencyProperty PresentedHeightProperty = DependencyProperty.Register(
        "PresentedHeight", typeof(double), typeof(TranslationHeightHost),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    private bool _measured;
    private double _targetHeight;
    private int _version;

    public TranslationHeightHost() => ClipToBounds = true;

    internal bool MotionEnabled { get; set; }
    internal bool IsMorphing { get; private set; }
    internal double TargetHeight => _targetHeight;
    internal double PresentedHeight => (double)GetValue(PresentedHeightProperty);
    // Tests can pause and seek the real timeline without changing production timing.
    internal Func<DoubleAnimation, AnimationClock>? AnimationClockFactory { get; set; }
    internal event Action? TargetHeightChanged;
    internal event Action? MorphCompleted;

    internal void Freeze()
    {
        double height = PresentedHeight;
        ++_version;
        IsMorphing = false;
        BeginAnimation(PresentedHeightProperty, null);
        SetValue(PresentedHeightProperty, height);
    }

    internal void Settle()
    {
        ++_version;
        IsMorphing = false;
        BeginAnimation(PresentedHeightProperty, null);
        SetValue(PresentedHeightProperty, _targetHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var desired = Child?.DesiredSize ?? default;
        double target = desired.Height;
        if (!_measured || Math.Abs(target - _targetHeight) > .1)
        {
            double from = PresentedHeight;
            bool animate = _measured && MotionEnabled && IsVisible &&
                !AnimationConfig.ReduceMotion && AnimationConfig.ClientAreaAnimation;
            _measured = true;
            _targetHeight = target;
            int version = ++_version;
            BeginAnimation(PresentedHeightProperty, null);
            SetValue(PresentedHeightProperty, target);
            IsMorphing = animate && Math.Abs(from - target) > .1;
            if (IsMorphing)
            {
                var motion = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                };
                Timeline.SetDesiredFrameRate(motion, Math.Min(120, AnimationConfig.TargetFps));
                motion.Completed += (_, _) =>
                {
                    if (version != _version) return;
                    IsMorphing = false;
                    MorphCompleted?.Invoke();
                };
                if (AnimationClockFactory is { } createClock)
                    ApplyAnimationClock(PresentedHeightProperty, createClock(motion));
                else BeginAnimation(PresentedHeightProperty, motion);
            }
            TargetHeightChanged?.Invoke();
        }
        return new Size(desired.Width, Math.Max(0, PresentedHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, _targetHeight));
        return finalSize;
    }
}
