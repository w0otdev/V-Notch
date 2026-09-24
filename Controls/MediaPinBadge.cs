using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controls;

// Fills the thumbnail host so placement follows its animated dimensions.
// Only the pin is painted; it never intercepts thumbnail gestures.
public sealed class MediaPinBadge : FrameworkElement
{
    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(
        nameof(IsPinned), typeof(bool), typeof(MediaPinBadge),
        new PropertyMetadata(false, (sender, args) =>
            ((MediaPinBadge)sender).SetPinned((bool)args.NewValue, animate: true)));

    public bool IsPinned
    {
        get => (bool)GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(MediaPinBadge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Geometry Pin = CreatePin();
    private static readonly DropShadowEffect PinShadow = CreateShadow();
    private bool _pinned;

    private double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    public MediaPinBadge()
    {
        IsHitTestVisible = false;
        Effect = PinShadow;
        Unloaded += (_, _) => Snap();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Snap();
        };
    }

    public void SetPinned(bool pinned, bool animate)
    {
        if (_pinned == pinned) return;
        double from = Reveal;
        _pinned = pinned;
        Snap();
        if (!animate || !IsVisible || !IsLoaded || AnimationConfig.ReduceMotion) return;
        BeginAnimation(RevealProperty, new DoubleAnimation(from, pinned ? 1 : 0,
            TimeSpan.FromMilliseconds(pinned ? 240 : 160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    internal void FinishAnimation() => Snap();

    private void Snap()
    {
        BeginAnimation(RevealProperty, null);
        Reveal = _pinned ? 1 : 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double reveal = Math.Clamp(Reveal, 0, 1);
        double edge = Math.Min(ActualWidth, ActualHeight);
        if (reveal <= 0 || edge < 12) return;

        // Straddle the artwork corner; the pin lives outside its rounded clip.
        // Use the same anchor for stationary and transitioning thumbnails.
        double t = Math.Clamp((edge - 22) / 80, 0, 1);
        double diameter = 12 + 6 * t;
        double cornerOffset = 2 + t;
        var center = new Point(ActualWidth - cornerOffset, ActualHeight - cornerOffset);
        double scale = 0.8 + 0.2 * reveal;

        dc.PushOpacity(reveal);
        dc.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(35 - 12 * (1 - reveal)));
        dc.PushTransform(new ScaleTransform(diameter / 24, diameter / 24));
        dc.PushTransform(new TranslateTransform(-10, -10));
        dc.DrawGeometry(Brushes.White, null, Pin);
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static DropShadowEffect CreateShadow()
    {
        var shadow = new DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = 0.7,
            BlurRadius = 3,
            ShadowDepth = 1,
            Direction = 270,
            RenderingBias = RenderingBias.Performance
        };
        shadow.Freeze();
        return shadow;
    }

    private static Geometry CreatePin()
    {
        var geometry = Geometry.Parse(
            "M6,3 Q5,3 5,4 Q5,5 6,5 L7,5 L7,9 L4.5,11.5 Q4,12 4,13 L9,13 L9,17 L10,19 L11,17 L11,13 L16,13 Q16,12 15.5,11.5 L13,9 L13,5 L14,5 Q15,5 15,4 Q15,3 14,3 Z");
        geometry.Freeze();
        return geometry;
    }
}
