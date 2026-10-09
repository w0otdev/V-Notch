using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

internal static class GlassClipBuilder
{
    public static StreamGeometry? CreateClip(Size size, CornerRadius radius, double powerFactor = 2)
    {
        double w = size.Width;
        double h = size.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return null;
        double power = double.IsFinite(powerFactor) ? Math.Max(1.05, powerFactor) : 2;

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
            AddCorner(context, new Point(w, topRight), new Point(w - topRight, topRight), -Math.PI / 2, topRight, power);
            context.LineTo(new Point(w, h - bottomRight), true, false);
            AddCorner(context, new Point(w - bottomRight, h), new Point(w - bottomRight, h - bottomRight), 0, bottomRight, power);
            context.LineTo(new Point(bottomLeft, h), true, false);
            AddCorner(context, new Point(0, h - bottomLeft), new Point(bottomLeft, h - bottomLeft), Math.PI / 2, bottomLeft, power);
            context.LineTo(new Point(0, topLeft), true, false);
            AddCorner(context, new Point(topLeft, 0), new Point(topLeft, topLeft), Math.PI, topLeft, power);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double ClampRadius(double radius, double maxRadius) =>
        double.IsNaN(radius) ? 0 : Math.Clamp(radius, 0, maxRadius);

    private static void AddCorner(StreamGeometryContext context, Point end, Point center,
        double startAngle, double radius, double power)
    {
        if (radius > 0 && Math.Abs(power - 2) < 0.01)
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        else if (radius > 0)
        {
            // Match the shader's |x/r|^n + |y/r|^n = 1 boundary. Keep the
            // exact endpoint so floating-point trig cannot shift geometry bounds.
            const int segments = 64;
            for (int i = 1; i < segments; i++)
            {
                double angle = startAngle + Math.PI / 2 * i / segments;
                double x = Math.Cos(angle);
                double y = Math.Sin(angle);
                context.LineTo(new Point(
                    center.X + radius * Math.CopySign(Math.Pow(Math.Abs(x), 2 / power), x),
                    center.Y + radius * Math.CopySign(Math.Pow(Math.Abs(y), 2 / power), y)), true, false);
            }
            context.LineTo(end, true, false);
        }
        else
            context.LineTo(end, true, false);
    }
}
