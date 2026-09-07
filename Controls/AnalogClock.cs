using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VNotch.Controls;

public class AnalogClock : FrameworkElement
{
    private static readonly Brush HandBrush = CreateFrozenBrush(0xF5, 0xF5, 0xF7);
    private static readonly Brush MajorTickBrush = CreateFrozenBrush(0xC8, 0xC8, 0xCC);
    private static readonly Brush MinorTickBrush = CreateFrozenBrush(0x6E, 0x6E, 0x73);
    private static readonly Brush AccentBrush = CreateFrozenBrush(0xE0, 0x23, 0x1F);
    private static readonly Brush HubHoleBrush = CreateFrozenBrush(0x0A, 0x0A, 0x0A);
    private static readonly Pen MinorTickPen = CreateFrozenPen(MinorTickBrush, 1.0);
    private static readonly Pen MajorRulerPen = CreateFrozenPen(MajorTickBrush, 1.4);
    private static readonly Typeface DateTypeface = new(
        new FontFamily("pack://application:,,,/Fonts/#SF Pro Display"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _sweepStartDelay;
    private bool _isRunning;

    private const int SweepStartDelayMs = 480;
    private const double FacePadding = 4.0;

    public static readonly DependencyProperty ShowDateProperty =
        DependencyProperty.Register(
            nameof(ShowDate),
            typeof(bool),
            typeof(AnalogClock),
            new FrameworkPropertyMetadata(true, OnShowDateChanged));

    public bool ShowDate
    {
        get => (bool)GetValue(ShowDateProperty);
        set => SetValue(ShowDateProperty, value);
    }

    private static void OnShowDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnalogClock clock)
        {
            clock._staticFace = null;
            clock.InvalidateVisual();
        }
    }

    private DrawingGroup? _staticFace;
    private double _cachedR = -1, _cachedCx = -1, _cachedCy = -1, _cachedPpd = -1;
    private int _cachedDay = -1;

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    public AnalogClock()
    {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Animated);

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += (_, _) => InvalidateVisual();

        Loaded += (_, _) => UpdateRunningState();
        Unloaded += (_, _) => StopTimer();
        IsVisibleChanged += (_, _) => UpdateRunningState();
    }

    private void UpdateRunningState()
    {
        if (IsVisible)
            StartTimer();
        else
            StopTimer();
    }

    private void StartTimer()
    {
        if (_isRunning) return;
        _isRunning = true;

        InvalidateVisual();

        _sweepStartDelay ??= new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(SweepStartDelayMs)
        };
        _sweepStartDelay.Tick -= OnSweepStartDelayElapsed;
        _sweepStartDelay.Tick += OnSweepStartDelayElapsed;
        _sweepStartDelay.Start();
    }

    private void OnSweepStartDelayElapsed(object? sender, EventArgs e)
    {
        _sweepStartDelay?.Stop();
        if (!_isRunning) return;
        _timer.Start();
        InvalidateVisual();
    }

    private void StopTimer()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _sweepStartDelay?.Stop();
        _timer.Stop();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double r = CalculateFaceRadius(w, h);
        if (r <= 0) return;
        var center = new Point(w / 2.0, h / 2.0);

        DateTime now = DateTime.Now;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_staticFace == null || Math.Abs(_cachedR - r) > 0.001 || Math.Abs(_cachedCx - center.X) > 0.001 ||
            Math.Abs(_cachedCy - center.Y) > 0.001 || _cachedDay != now.Day || Math.Abs(_cachedPpd - ppd) > 0.001)
        {
            _staticFace = BuildStaticFace(center, r, now.Day, ppd);
            _cachedR = r;
            _cachedCx = center.X;
            _cachedCy = center.Y;
            _cachedDay = now.Day;
            _cachedPpd = ppd;
        }

        drawingContext.DrawDrawing(_staticFace);
        DrawHands(drawingContext, center, r, now);
        DrawCenterHub(drawingContext, center, r);
    }

    internal static double CalculateFaceRadius(double width, double height) =>
        Math.Max(0, Math.Min(width, height) / 2.0 - FacePadding);

    private DrawingGroup BuildStaticFace(Point center, double r, int day, double pixelsPerDip)
    {
        bool showDate = ShowDate;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            DrawTicks(dc, center, r);
            DrawHourMarkers(dc, center, r, skipThree: showDate);
            if (showDate)
                DrawDate(dc, center, r, day, pixelsPerDip);
        }
        group.Freeze();
        return group;
    }

    private static void DrawTicks(DrawingContext dc, Point center, double r)
    {
        double outer = r * 0.97;
        for (int i = 0; i < 60; i++)
        {
            bool isFive = i % 5 == 0;
            double angle = i * 6.0 * Math.PI / 180.0;
            double sin = Math.Sin(angle);
            double cos = Math.Cos(angle);

            double innerLen = isFive ? r * 0.86 : r * 0.91;
            var p1 = new Point(center.X + sin * innerLen, center.Y - cos * innerLen);
            var p2 = new Point(center.X + sin * outer, center.Y - cos * outer);
            dc.DrawLine(isFive ? MajorRulerPen : MinorTickPen, p1, p2);
        }
    }

    private static void DrawHourMarkers(DrawingContext dc, Point center, double r, bool skipThree)
    {
        double markerLen = r * 0.18;
        double thickness = Math.Max(2.0, r * 0.075);
        double markerCenter = r * 0.70;

        for (int h = 0; h < 12; h++)
        {
            if (skipThree && h == 3) continue;

            double angle = h * 30.0;
            dc.PushTransform(new RotateTransform(angle, center.X, center.Y));
            var rect = new Rect(
                center.X - thickness / 2.0,
                center.Y - (markerCenter + markerLen / 2.0),
                thickness,
                markerLen);
            dc.DrawRoundedRectangle(MajorTickBrush, null, rect, thickness / 2.0, thickness / 2.0);
            dc.Pop();
        }
    }

    private static void DrawDate(DrawingContext dc, Point center, double r, int day, double pixelsPerDip)
    {
        double fontSize = r * 0.30;
        var text = new FormattedText(
            day.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            DateTypeface,
            fontSize,
            AccentBrush,
            pixelsPerDip);

        double cx = center.X + r * 0.56;
        double cy = center.Y;
        var origin = new Point(cx - text.Width / 2.0, cy - text.Height / 2.0);
        dc.DrawText(text, origin);
    }

    private static void DrawHands(DrawingContext dc, Point center, double r, DateTime now)
    {
        double seconds = now.Second + now.Millisecond / 1000.0;
        double minutes = now.Minute + seconds / 60.0;
        double hours = (now.Hour % 12) + minutes / 60.0;

        double hourAngle = hours * 30.0;
        double minuteAngle = minutes * 6.0;
        double secondAngle = seconds * 6.0;

        DrawHand(dc, center, hourAngle, HandBrush,
            length: r * 0.52, tail: r * 0.11, thickness: Math.Max(2.5, r * 0.085));

        DrawHand(dc, center, minuteAngle, HandBrush,
            length: r * 0.74, tail: r * 0.13, thickness: Math.Max(2.0, r * 0.065));

        DrawHand(dc, center, secondAngle, AccentBrush,
            length: r * 0.82, tail: r * 0.22, thickness: Math.Max(1.2, r * 0.022));
    }

    private static void DrawHand(DrawingContext dc, Point center, double angleDeg, Brush brush,
        double length, double tail, double thickness)
    {
        dc.PushTransform(new RotateTransform(angleDeg, center.X, center.Y));
        var rect = new Rect(
            center.X - thickness / 2.0,
            center.Y - length,
            thickness,
            length + tail);
        dc.DrawRoundedRectangle(brush, null, rect, thickness / 2.0, thickness / 2.0);
        dc.Pop();
    }

    private static void DrawCenterHub(DrawingContext dc, Point center, double r)
    {
        double hub = Math.Max(2.5, r * 0.06);
        dc.DrawEllipse(AccentBrush, null, center, hub, hub);
        dc.DrawEllipse(HubHoleBrush, null, center, hub * 0.42, hub * 0.42);
    }
}
