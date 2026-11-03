// Frozen 2026-11-03 renderer for paired allocation benchmarks and pixel regression.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Tests.Fixtures;

public partial class BeforeMemoryMorphingSettingsIcon : FrameworkElement
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

    [LibraryImport("VNotch.Native")]
    private static partial int vn_morph_settings_icon_sample_contour(
        ReadOnlySpan<double> rawXy,
        int rawPointCount,
        ReadOnlySpan<double> matrix6,
        byte isClosed,
        int sampleCount,
        Span<double> outSampledXy,
        int outCapDoubles,
        out double outCenterX,
        out double outCenterY,
        out double outMinX,
        out double outBboxArea);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_morph_settings_icon_compute_points(
        ReadOnlySpan<double> fromXy,
        int fromPointCount,
        ReadOnlySpan<double> toXy,
        int toPointCount,
        double centerX,
        double centerY,
        double progress,
        int sampleCount,
        Span<double> outXy,
        int outCapDoubles,
        out int outPointCount);

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(BeforeMemoryMorphingSettingsIcon),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(BeforeMemoryMorphingSettingsIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Brush
    {
        get => (Brush?)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Geometry), typeof(BeforeMemoryMorphingSettingsIcon),
        new FrameworkPropertyMetadata(null, (d, e) =>
        {
            if (d is BeforeMemoryMorphingSettingsIcon icon && e.NewValue is Geometry g)
            {
                icon.MorphTo(g, animate: icon.IsLoaded && !AnimationConfig.ReduceMotion);
            }
        }));

    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public BeforeMemoryMorphingSettingsIcon()
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
            _from = Sample(previous, out var fromCenters);
            _to = Sample(normalized, out var toCenters);
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
                _collapseCenters[i] = i < _from.Length ? fromCenters[i] : toCenters[i];
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

    private Point[][] Sample(Geometry geometry, out Point[] sortedCenters)
    {
        var flat = geometry.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
        Matrix transform = flat.Transform?.Value ?? Matrix.Identity;
        Span<double> matrix6 = stackalloc double[6]
        {
            transform.M11,
            transform.M12,
            transform.M21,
            transform.M22,
            transform.OffsetX,
            transform.OffsetY
        };

        var contours = new List<Point[]>(flat.Figures.Count);
        var centers = new List<Point>(flat.Figures.Count);
        var sortKeys = new List<double>(flat.Figures.Count);
        Span<double> sampledXy = stackalloc double[Samples * 2];
        Span<double> stackRawXy = stackalloc double[512];

        for (int f = 0; f < flat.Figures.Count; f++)
        {
            var figure = flat.Figures[f];
            int rawCount = 1;
            for (int s = 0; s < figure.Segments.Count; s++)
            {
                var segment = figure.Segments[s];
                if (segment is LineSegment) rawCount++;
                else if (segment is PolyLineSegment polyline) rawCount += polyline.Points.Count;
            }
            if (rawCount < 2) continue;

            Span<double> rawXy = rawCount <= 256 ? stackRawXy[..(rawCount * 2)] : new double[rawCount * 2];

            int writeIdx = 0;
            rawXy[writeIdx++] = figure.StartPoint.X;
            rawXy[writeIdx++] = figure.StartPoint.Y;
            for (int s = 0; s < figure.Segments.Count; s++)
            {
                var segment = figure.Segments[s];
                if (segment is LineSegment line)
                {
                    rawXy[writeIdx++] = line.Point.X;
                    rawXy[writeIdx++] = line.Point.Y;
                }
                else if (segment is PolyLineSegment polyline)
                {
                    var pts = polyline.Points;
                    for (int p = 0; p < pts.Count; p++)
                    {
                        var pt = pts[p];
                        rawXy[writeIdx++] = pt.X;
                        rawXy[writeIdx++] = pt.Y;
                    }
                }
            }

            int status = vn_morph_settings_icon_sample_contour(
                rawXy,
                writeIdx / 2,
                matrix6,
                figure.IsClosed ? (byte)1 : (byte)0,
                Samples,
                sampledXy,
                sampledXy.Length,
                out double centerX,
                out double centerY,
                out double minX,
                out double bboxArea);

            if (status != 0) continue;

            var points = new Point[Samples];
            sampledXy.CopyTo(MemoryMarshal.Cast<Point, double>(points.AsSpan()));
            contours.Add(points);
            centers.Add(new Point(centerX, centerY));
            sortKeys.Add(IsTextGeometry ? minX : -bboxArea);
        }

        int count = contours.Count;
        if (count == 0)
        {
            sortedCenters = Array.Empty<Point>();
            return Array.Empty<Point[]>();
        }

        var resultContours = contours.ToArray();
        sortedCenters = centers.ToArray();
        Span<int> order = count <= 64 ? stackalloc int[count] : new int[count];
        for (int i = 0; i < count; i++) order[i] = i;

        // Stable insertion sort matching Enumerable.OrderBy / OrderByDescending
        for (int i = 1; i < count; i++)
        {
            int pos = order[i];
            double key = sortKeys[pos];
            int j = i - 1;
            while (j >= 0 && sortKeys[order[j]] > key)
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = pos;
        }

        var orderedContours = new Point[count][];
        var orderedCenters = new Point[count];
        for (int i = 0; i < count; i++)
        {
            int srcIdx = order[i];
            orderedContours[i] = resultContours[srcIdx];
            orderedCenters[i] = sortedCenters[srcIdx];
        }

        sortedCenters = orderedCenters;
        return orderedContours;
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
            Span<double> coords = stackalloc double[Samples * 2];
            for (int i = 0; i < shape.Figures.Count; i++)
            {
                ReadOnlySpan<double> source = i < _from.Length
                    ? MemoryMarshal.Cast<Point, double>(_from[i].AsSpan())
                    : ReadOnlySpan<double>.Empty;
                ReadOnlySpan<double> destination = i < _to.Length
                    ? MemoryMarshal.Cast<Point, double>(_to[i].AsSpan())
                    : ReadOnlySpan<double>.Empty;
                var center = _collapseCenters[i];
                var figure = shape.Figures[i];
                var points = _framePoints[i];

                vn_morph_settings_icon_compute_points(
                    source,
                    source.Length / 2,
                    destination,
                    destination.Length / 2,
                    center.X,
                    center.Y,
                    progress,
                    Samples,
                    coords,
                    coords.Length,
                    out _);

                figure.StartPoint = new Point(coords[0], coords[1]);
                coords.Slice(2).CopyTo(MemoryMarshal.Cast<Point, double>(points.AsSpan()));

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
            (Math.Abs(_renderTransform.Value.M11 - RenderSize.Width / 56) > 1e-6 ||
             Math.Abs(_renderTransform.Value.M22 - RenderSize.Height / 56) > 1e-6))
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
