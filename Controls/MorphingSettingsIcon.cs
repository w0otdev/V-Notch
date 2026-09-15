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
    private Point[][] _from = Array.Empty<Point[]>();
    private Point[][] _to = Array.Empty<Point[]>();
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(MorphingSettingsIcon),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public MorphingSettingsIcon()
    {
        Unloaded += (_, _) =>
        {
            BeginAnimation(ProgressProperty, null);
            SetValue(ProgressProperty, 1d);
            _display = _target;
        };
    }

    public void MorphTo(Geometry geometry)
    {
        var next = geometry.Clone();
        Rect bounds = next.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        double scale = 48 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.Identity;
        matrix.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
        matrix.Scale(scale, scale);
        matrix.Translate(28, 28);
        if (IsTextGeometry) matrix = Matrix.Identity;
        var normalized = new GeometryGroup { Transform = new MatrixTransform(matrix) };
        normalized.Children.Add(next);
        normalized.Freeze();

        var previous = _display;
        _target = normalized;
        BeginAnimation(ProgressProperty, null);
        SetValue(ProgressProperty, 1d);
        if (!IsLoaded || previous == null || AnimationConfig.ReduceMotion)
        {
            _display = _target;
            InvalidateVisual();
            return;
        }
        _from = Sample(previous);
        _to = Sample(normalized);
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_target == null) return;
        double progress = (double)GetValue(ProgressProperty);
        if (progress >= 1)
            _display = _target;
        else
        {
            var shape = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var writer = shape.Open())
            {
                for (int i = 0; i < Math.Max(_from.Length, _to.Length); i++)
                {
                    var source = i < _from.Length ? _from[i] : null;
                    var destination = i < _to.Length ? _to[i] : null;
                    var existing = source ?? destination!;
                    var center = new Point(existing.Average(p => p.X), existing.Average(p => p.Y));
                    for (int j = 0; j < Samples; j++)
                    {
                        var a = source?[j] ?? center;
                        var b = destination?[j] ?? center;
                        Point point = a + (b - a) * progress;
                        if (j == 0) writer.BeginFigure(point, true, true);
                        else writer.LineTo(point, false, false);
                    }
                }
            }
            shape.Freeze();
            _display = shape;
        }
        drawingContext.PushTransform(IsTextGeometry ? Transform.Identity :
            new ScaleTransform(ActualWidth / 56, ActualHeight / 56));
        drawingContext.DrawGeometry(Brushes.White, null, _display);
        drawingContext.Pop();
    }
}
