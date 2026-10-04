using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

internal static class GlassClipBuilder
{
    public static StreamGeometry? CreateClip(Size size, CornerRadius radius)
    {
        double w = size.Width;
        double h = size.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return null;

        // Keep the notch silhouette inside its bounds during animated resizing.
        double maxRadius = Math.Min(w, h) / 2;
        double topLeft = ClampRadius(radius.TopLeft, maxRadius);
        double topRight = ClampRadius(radius.TopRight, maxRadius);
        double bottomRight = ClampRadius(radius.BottomRight, maxRadius);
        double bottomLeft = ClampRadius(radius.BottomLeft, maxRadius);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(topLeft, 0), true, true);
            context.LineTo(new Point(w - topRight, 0), true, false);
            AddCorner(context, new Point(w, topRight), topRight);
            context.LineTo(new Point(w, h - bottomRight), true, false);
            AddCorner(context, new Point(w - bottomRight, h), bottomRight);
            context.LineTo(new Point(bottomLeft, h), true, false);
            AddCorner(context, new Point(0, h - bottomLeft), bottomLeft);
            context.LineTo(new Point(0, topLeft), true, false);
            AddCorner(context, new Point(topLeft, 0), topLeft);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double ClampRadius(double radius, double maxRadius) =>
        double.IsNaN(radius) ? 0 : Math.Clamp(radius, 0, maxRadius);

    private static void AddCorner(StreamGeometryContext context, Point end, double radius)
    {
        if (radius > 0)
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        else
            context.LineTo(end, true, false);
    }
}
