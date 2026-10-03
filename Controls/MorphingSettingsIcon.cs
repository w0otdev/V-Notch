using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public class MorphingSettingsIcon : FrameworkElement
{
    protected virtual bool IsTextGeometry => false;
    private const int Samples = 96;
    private Geometry? _target;
    private Geometry? _display;
    private Func<double, Geometry>? _geometryAtProgress;
    private Transform _morphTransform = Transform.Identity;
    private Point[][] _from = Array.Empty<Point[]>();
    private Point[][] _to = Array.Empty<Point[]>();
    private Point[] _collapseCenters = Array.Empty<Point>();
    private Point[][] _framePoints = Array.Empty<Point[]>();
    private PathGeometry _morphGeometry = new() { FillRule = FillRule.Nonzero };
    private GeometryGroup? _progressGeometry;
    private Transform _renderTransform = Transform.Identity;
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(MorphingSettingsIcon),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(MorphingSettingsIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Brush
    {
        get => (Brush?)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Geometry), typeof(MorphingSettingsIcon),
        new FrameworkPropertyMetadata(null, (d, e) =>
        {
            if (d is MorphingSettingsIcon icon && e.NewValue is Geometry g)
            {
                icon.MorphTo(g, animate: icon.IsLoaded && !AnimationConfig.ReduceMotion);
            }
        }));

    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public MorphingSettingsIcon()
    {
        Unloaded += (_, _) =>
        {
            BeginAnimation(ProgressProperty, null);
            SetValue(ProgressProperty, 1d);
            _display = _target;
            _geometryAtProgress = null;
        };
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    public void MorphTo(Geometry geometry, bool animate = true, Rect? viewport = null,
        TimeSpan? duration = null, IEasingFunction? easing = null,
        Func<double, Geometry>? geometryAtProgress = null)
    {
        var next = geometry.Clone();
        Rect bounds = viewport ?? next.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        double scale = 48 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.Identity;
        matrix.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
        matrix.Scale(scale, scale);
        matrix.Translate(28, 28);
        if (IsTextGeometry) matrix = Matrix.Identity;
        var normalized = new GeometryGroup { FillRule = FillRule.Nonzero, Transform = new MatrixTransform(matrix) };
        normalized.Children.Add(next);
        normalized.Freeze();

        var previous = _display;
        _geometryAtProgress = null;
        _target = normalized;
        BeginAnimation(ProgressProperty, null);
        SetValue(ProgressProperty, 1d);
        if (!animate || !IsLoaded || previous == null || AnimationConfig.ReduceMotion)
        {
            _display = _target;
            InvalidateVisual();
            return;
        }
        _geometryAtProgress = geometryAtProgress;
        _morphTransform = normalized.Transform;
        if (geometryAtProgress == null)
        {
            _from = Sample(previous);
            _to = Sample(normalized);
            _collapseCenters = new Point[Math.Max(_from.Length, _to.Length)];
            _framePoints = new Point[_collapseCenters.Length][];
            _morphGeometry = new PathGeometry { FillRule = FillRule.Nonzero };
            for (int i = 0; i < _collapseCenters.Length; i++)
            {
                var segment = new PolyLineSegment { IsStroked = false };
                _framePoints[i] = new Point[Samples - 1];
                segment.Points = new PointCollection(_framePoints[i]);
                var figure = new PathFigure { IsFilled = true, IsClosed = true };
                figure.Segments.Add(segment);
                _morphGeometry.Figures.Add(figure);
                if (i < _from.Length && i < _to.Length) continue;
                var points = i < _from.Length ? _from[i] : _to[i];
                double x = 0, y = 0;
                foreach (var point in points)
                {
                    x += point.X;
                    y += point.Y;
                }
                _collapseCenters[i] = new Point(x / points.Length, y / points.Length);
            }
        }
        else
        {
            _progressGeometry = new GeometryGroup { Transform = _morphTransform };
        }
        var animation = new DoubleAnimation(0, 1, duration ?? TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        BeginAnimation(ProgressProperty, animation);
    }

    private Point[][] Sample(Geometry geometry)
    {
        var flat = geometry.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
        // Flattened geometry may have no transform; its points are already in place.
        Matrix transform = flat.Transform?.Value ?? Matrix.Identity;
        var contours = new List<Point[]>();
        foreach (var figure in flat.Figures)
        {
            var vertices = new List<Point>();
            void AddVertex(Point point)
            {
                point = transform.Transform(point);
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
                if (vertices.Count == 0 || (point - vertices[^1]).Length > 0.000001)
                    vertices.Add(point);
            }

            AddVertex(figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line) AddVertex(line.Point);
                else if (segment is PolyLineSegment polyline)
                    foreach (var point in polyline.Points) AddVertex(point);
            }
            if (vertices.Count < 2) continue;
            if (figure.IsClosed && (vertices[^1] - vertices[0]).Length > 0.000001)
                vertices.Add(vertices[0]);

            // Sample by cumulative length in managed code. WPF's native
            // GetPointAtFractionLength rejects degenerate SVG subpaths (e.g. h0 Z).
            var lengths = new double[vertices.Count];
            for (int i = 1; i < vertices.Count; i++)
                lengths[i] = lengths[i - 1] + (vertices[i] - vertices[i - 1]).Length;
            double totalLength = lengths[^1];
            if (!double.IsFinite(totalLength) || totalLength <= 0.000001) continue;

            var points = new Point[Samples];
            int edge = 1;
            for (int i = 0; i < Samples; i++)
            {
                double distance = totalLength * i / Samples;
                while (edge < lengths.Length - 1 && lengths[edge] < distance) edge++;
                double fraction = (distance - lengths[edge - 1]) / (lengths[edge] - lengths[edge - 1]);
                points[i] = vertices[edge - 1] + (vertices[edge] - vertices[edge - 1]) * fraction;
            }
            contours.Add(points);
        }
        if (IsTextGeometry)
            return contours.OrderBy(points => points.Min(p => p.X)).ToArray();
        return contours.OrderByDescending(points =>
            (points.Max(p => p.X) - points.Min(p => p.X)) *
            (points.Max(p => p.Y) - points.Min(p => p.Y))).ToArray();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var transform = new ScaleTransform(sizeInfo.NewSize.Width / 56, sizeInfo.NewSize.Height / 56);
        transform.Freeze();
        _renderTransform = transform;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_target == null) return;
        double progress = (double)GetValue(ProgressProperty);
        if (progress >= 1)
        {
            _display = _target;
            _geometryAtProgress = null;
        }
        else if (_geometryAtProgress != null)
        {
            var shape = _progressGeometry!;
            shape.Children.Clear();
            shape.Children.Add(_geometryAtProgress(progress));
            _display = shape;
        }
        else
        {
            var shape = _morphGeometry;
            for (int i = 0; i < shape.Figures.Count; i++)
            {
                var source = i < _from.Length ? _from[i] : null;
                var destination = i < _to.Length ? _to[i] : null;
                var center = _collapseCenters[i];
                var figure = shape.Figures[i];
                var points = _framePoints[i];
                for (int j = 0; j < Samples; j++)
                {
                    var a = source?[j] ?? center;
                    var b = destination?[j] ?? center;
                    Point point = a + (b - a) * progress;
                    if (j == 0) figure.StartPoint = point;
                    else points[j - 1] = point;
                }
                // Publish all points together. Mutating a live PointCollection
                // individually propagates 95 Freezable notifications per contour.
                var collection = new PointCollection(points);
                collection.Freeze();
                ((PolyLineSegment)figure.Segments[0]).Points = collection;
            }
            _display = shape;
        }
        // RenderSize is authoritative even before the deferred SizeChanged callback.
        // Keep the normalized 56-unit geometry inside the current layout slot.
        if (!IsTextGeometry &&
            (_renderTransform.Value.M11 != RenderSize.Width / 56 ||
             _renderTransform.Value.M22 != RenderSize.Height / 56))
        {
            var transform = new ScaleTransform(RenderSize.Width / 56, RenderSize.Height / 56);
            transform.Freeze();
            _renderTransform = transform;
        }
        drawingContext.PushTransform(IsTextGeometry ? Transform.Identity : _renderTransform);
        drawingContext.DrawGeometry(Brush ?? VNotch.Services.UiPalette.PrimaryBrush, null, _display);
        drawingContext.Pop();
    }
}
