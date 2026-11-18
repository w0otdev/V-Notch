using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Services;

namespace VNotch.Controls;

// A traveling feather blends the departing page from sharp to soft to transparent.
// The destination underneath is live as soon as the selection changes.
public sealed class ChangelogBlurWave : FrameworkElement
{
    private const int MaxSnapshotPixels = 640 * 1024;
    private const int BlurSampleSpacing = 7;
    private static readonly Brush Backdrop = FrozenBackdrop();
    private readonly DrawingGroup _drawing = new();
    private readonly TranslateTransform _travel = new();
    private DependencyProperty _axis = TranslateTransform.XProperty;
    private AnimationClock? _clock;
    private double _distance;
    private long _lastRequest;

    internal double Progress => _clock != null ? (double)_travel.GetValue(_axis) / _distance : 0;

    public ChangelogBlurWave()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Visibility = Visibility.Hidden;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Stop(); };
    }

    internal void Capture(FrameworkElement viewport, Orientation axis, bool forward)
    {
        var size = RenderSize;
        long now = Stopwatch.GetTimestamp();
        bool rapid = _lastRequest != 0 && Stopwatch.GetElapsedTime(_lastRequest, now).TotalMilliseconds < 110;
        _lastRequest = now;
        if (rapid || !IsEnabled || !viewport.IsVisible || size.Width < 1 || size.Height < 1 ||
            AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            Stop();
            return;
        }

        var bounds = new Rect(size);
        double scale = Math.Min(VisualTreeHelper.GetDpi(viewport).DpiScaleX,
            Math.Sqrt(MaxSnapshotPixels / (size.Width * size.Height)));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Backdrop, null, bounds);
            var viewbox = new Rect(TranslatePoint(new Point(), viewport) + VisualTreeHelper.GetOffset(viewport), size);
            dc.DrawRectangle(new VisualBrush(viewport)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewbox, Stretch = Stretch.Fill,
                AutoLayoutContent = false
            }, null, bounds);
        }
        var sharp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        sharp.Render(visual);
        sharp.Freeze();
        var soft = MakeSoftSample(sharp, size);
        Stop();

        bool vertical = axis == Orientation.Vertical;
        double length = vertical ? size.Height : size.Width;
        double width = Math.Min(length, Math.Clamp(length * .30, 64, 168));
        double start = forward ? -width : length;
        var band = vertical ? new Rect(0, start, size.Width, width) : new Rect(start, 0, width, size.Height);
        var tail = vertical ? new Point(0, forward ? band.Top : band.Bottom) : new Point(forward ? band.Left : band.Right, 0);
        var head = vertical ? new Point(0, forward ? band.Bottom : band.Top) : new Point(forward ? band.Right : band.Left, 0);
        using (var dc = _drawing.Open())
        {
            dc.PushClip(new RectangleGeometry(bounds));
            dc.PushClip(new RectangleGeometry(band) { Transform = _travel });
            DrawFeather(dc, soft, bounds, tail, head, 0, .46);
            DrawFeather(dc, sharp, bounds, tail, head, .40, .98);
            dc.Pop();
            dc.PushClip(new RectangleGeometry(bounds) { Transform = _travel });
            dc.DrawImage(sharp, bounds);
            dc.Pop();
            dc.Pop();
        }

        _axis = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        _distance = (forward ? 1 : -1) * (length + width);
        var sweep = AnimationPrimitives.MakeAnim(0, _distance,
            vertical ? AnimationPrimitives._dur350 : AnimationPrimitives._dur400, AnimationPrimitives._easeAppleInOut);
        var clock = sweep.CreateClock();
        clock.Completed += (_, _) => { if (ReferenceEquals(_clock, clock)) Stop(); };
        _clock = clock;
        Visibility = Visibility.Visible;
        AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
        _travel.ApplyAnimationClock(_axis, clock);
    }

    private void DrawFeather(DrawingContext dc, BitmapSource image, Rect bounds,
        Point tail, Point head, double from, double to)
    {
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = tail, EndPoint = head, Transform = _travel
        };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, from));
        mask.GradientStops.Add(new GradientStop(Colors.Black, to));
        dc.PushOpacityMask(mask);
        dc.DrawImage(image, bounds);
        dc.Pop();
    }

    private static BitmapSource MakeSoftSample(BitmapSource sharp, Size size)
    {
        int w = Math.Max(1, (int)Math.Ceiling(size.Width / BlurSampleSpacing));
        int h = Math.Max(1, (int)Math.Ceiling(size.Height / BlurSampleSpacing));
        // Filter the source pixels before the moving band scales the sample back up.
        var soft = new TransformedBitmap(sharp,
            new ScaleTransform((double)w / sharp.PixelWidth, (double)h / sharp.PixelHeight));
        soft.Freeze();
        return soft;
    }

    internal void Stop()
    {
        if (_clock == null) return;
        _clock = null;
        AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
        _travel.ApplyAnimationClock(_axis, null);
        _drawing.Children.Clear();
        Visibility = Visibility.Hidden;
    }

    private void OnReduceMotionChanged() { if (AnimationConfig.ReduceMotion) Stop(); }
    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_drawing);
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Stop();
    }

    private static Brush FrozenBackdrop()
    {
        var brush = new SolidColorBrush(Color.FromRgb(9, 9, 10));
        brush.Freeze();
        return brush;
    }
}
