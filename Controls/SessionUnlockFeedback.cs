using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch.Controls;

// Temporary layer above artwork: media can keep updating underneath it.
public sealed class SessionUnlockFeedback : Grid
{
    // Reserve space for the upright shackle to swing outside the body's right edge.
    private static readonly Rect IconViewport = new(0, 0, 28, 24);
    private const string Body = "M6,10 H18 Q20,10 20,12 V20 Q20,22 18,22 H6 Q4,22 4,20 V12 Q4,10 6,10 Z ";
    private static readonly Geometry BodyGeometry = Geometry.Parse(Body);
    private static readonly Pen ShacklePen = CreateShacklePen();
    private static readonly Geometry ClosedLock = CreateLockGeometry(0);
    private static readonly Geometry OpenLock = CreateLockGeometry(1);
    private const int ClosedHoldMs = 350;
    private const int OpenMs = 800;
    private const int OpenHoldMs = 1100;
    private const int FadeMs = 250;
    private readonly MorphingSettingsIcon _icon = new() { Width = 56, Height = 56 };
    private readonly DispatcherTimer _timer;
    private int _stage;
    private int _generation;

    public SessionUnlockFeedback()
    {
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        ClipToBounds = true;
        Background = Brushes.Black;
        Children.Add(new Viewbox
        {
            Child = _icon,
            MaxWidth = 56,
            MaxHeight = 56,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _timer.Tick += OnTick;
        Unloaded += (_, _) => Stop();
    }

    public void Play()
    {
        Stop();
        Visibility = Visibility.Visible;
        Opacity = 1;
        _stage = 0;
        _icon.MorphTo(AnimationConfig.ReduceMotion ? OpenLock : ClosedLock,
            animate: false, viewport: IconViewport);
        if (!AnimationConfig.ReduceMotion)
        {
            var reveal = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            Timeline.SetDesiredFrameRate(reveal, AnimationConfig.TargetFps);
            BeginAnimation(OpacityProperty, reveal);
        }
        Schedule(AnimationConfig.ReduceMotion ? 2500 : ClosedHoldMs);
    }

    public void Stop()
    {
        ++_generation;
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        _icon.MorphTo(ClosedLock, animate: false, viewport: IconViewport);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (AnimationConfig.ReduceMotion)
        {
            Stop();
            return;
        }
        if (_stage++ == 0)
        {
            _icon.MorphTo(OpenLock, viewport: IconViewport,
                duration: TimeSpan.FromMilliseconds(OpenMs),
                easing: new CubicEase { EasingMode = EasingMode.EaseInOut },
                geometryAtProgress: CreateLockGeometry);
            Schedule(OpenMs + OpenHoldMs);
            return;
        }
        int generation = _generation;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        fade.Completed += (_, _) => { if (generation == _generation) Stop(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Schedule(int milliseconds)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _timer.Start();
    }

    private static Pen CreateShacklePen()
    {
        var pen = new Pen(Brushes.White, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Geometry CreateLockGeometry(double progress)
    {
        // Orthographic projection of a 180-degree turn about the right leg's
        // vertical axis. Every point on x=16 stays fixed throughout the turn.
        double cosine = Math.Cos(Math.PI * Math.Clamp(progress, 0, 1));
        Point Project(double x, double y) => new(16 + (x - 16) * cosine, y);
        var centerline = new StreamGeometry();
        using (var path = centerline.Open())
        {
            path.BeginFigure(Project(8, 10), false, false);
            path.LineTo(Project(8, 7), true, false);
            path.BezierTo(Project(8, 4.790861), Project(9.790861, 3), Project(12, 3), true, false);
            path.BezierTo(Project(14.209139, 3), Project(16, 4.790861), Project(16, 7), true, false);
            path.LineTo(Project(16, 10), true, false);
        }
        // Widen after projection so the metal retains thickness when edge-on.
        var shackle = centerline.GetWidenedPathGeometry(ShacklePen);
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        geometry.Children.Add(BodyGeometry);
        geometry.Children.Add(shackle);
        geometry.Freeze();
        return geometry;
    }
}
