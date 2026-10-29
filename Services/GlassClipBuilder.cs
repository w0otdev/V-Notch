using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace VNotch.Services;

internal static partial class GlassClipBuilder
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_glass_compute_squircle_lut")]
    private static unsafe partial int NativeComputeSquircleLut(
        double power,
        int segments,
        double* outPowCos,
        double* outPowSin);

    // The shell, hover glow and material host request the same silhouette in
    // one layout pass. Share the frozen resource without retaining old frames.
    [ThreadStatic] private static StreamGeometry? _lastClip;
    [ThreadStatic] private static Size _lastClipSize;
    [ThreadStatic] private static CornerRadius _lastClipRadius;
    [ThreadStatic] private static double _lastClipPower;
    private const int CornerSegments = 64;

    private static double _cachedPower = double.NaN;
    private static readonly double[] _cachedPowCos = new double[CornerSegments];
    private static readonly double[] _cachedPowSin = new double[CornerSegments];
    private static readonly object _lutLock = new();

    private static unsafe (double[] PowCos, double[] PowSin) GetUnitCurve(double power)
    {
        lock (_lutLock)
        {
            if (Math.Abs(power - _cachedPower) < 1e-6)
                return (_cachedPowCos, _cachedPowSin);

            fixed (double* cosPtr = _cachedPowCos, sinPtr = _cachedPowSin)
            {
                NativeComputeSquircleLut(power, CornerSegments, cosPtr, sinPtr);
            }
            _cachedPower = power;
            return (_cachedPowCos, _cachedPowSin);
        }
    }

    public static StreamGeometry? CreateClip(Size size, CornerRadius radius, double powerFactor = 2)
    {
        double w = size.Width;
        double h = size.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return null;
        double power = double.IsFinite(powerFactor) ? Math.Max(1.05, powerFactor) : 2;
        if (_lastClip != null && size == _lastClipSize && radius == _lastClipRadius && Math.Abs(power - _lastClipPower) < 1e-6)
            return _lastClip;

        // Keep the notch silhouette inside its bounds during animated resizing.
        double maxRadius = Math.Min(w, h) / 2;
        double topLeft = ClampRadius(radius.TopLeft, maxRadius);
        double topRight = ClampRadius(radius.TopRight, maxRadius);
        double bottomRight = ClampRadius(radius.BottomRight, maxRadius);
        double bottomLeft = ClampRadius(radius.BottomLeft, maxRadius);

        var (powCos, powSin) = Math.Abs(power - 2) >= 0.01
            ? GetUnitCurve(power)
            : (System.Array.Empty<double>(), System.Array.Empty<double>());

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(topLeft, 0), true, true);
            context.LineTo(new Point(w - topRight, 0), false, false);
            AddCorner(context, new Point(w, topRight), new Point(w - topRight, topRight), 0, topRight, power, powCos, powSin);
            context.LineTo(new Point(w, h - bottomRight), false, false);
            AddCorner(context, new Point(w - bottomRight, h), new Point(w - bottomRight, h - bottomRight), 1, bottomRight, power, powCos, powSin);
            context.LineTo(new Point(bottomLeft, h), false, false);
            AddCorner(context, new Point(0, h - bottomLeft), new Point(bottomLeft, h - bottomLeft), 2, bottomLeft, power, powCos, powSin);
            context.LineTo(new Point(0, topLeft), false, false);
            AddCorner(context, new Point(topLeft, 0), new Point(topLeft, topLeft), 3, topLeft, power, powCos, powSin);
        }
        geometry.Freeze();
        _lastClipSize = size;
        _lastClipRadius = radius;
        _lastClipPower = power;
        _lastClip = geometry;
        return geometry;
    }

    private static double ClampRadius(double radius, double maxRadius) =>
        double.IsNaN(radius) ? 0 : Math.Clamp(radius, 0, maxRadius);

    private static void AddCorner(StreamGeometryContext context, Point end, Point center,
        int quadrant, double radius, double power, double[] powCos, double[] powSin)
    {
        if (radius > 0 && Math.Abs(power - 2) < 0.01)
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
        else if (radius > 0)
        {
            // Match the shader's |x/r|^n + |y/r|^n = 1 boundary using cached unit quadrant curve.
            for (int i = 1; i < CornerSegments; i++)
            {
                double c = powCos[i];
                double s = powSin[i];
                double dx, dy;
                switch (quadrant)
                {
                    case 0: // Top-Right: startAngle = -PI/2 -> cos(t - PI/2) = sin(t), sin(t - PI/2) = -cos(t)
                        dx = s; dy = -c; break;
                    case 1: // Bottom-Right: startAngle = 0 -> cos(t), sin(t)
                        dx = c; dy = s; break;
                    case 2: // Bottom-Left: startAngle = PI/2 -> cos(t + PI/2) = -sin(t), sin(t + PI/2) = cos(t)
                        dx = -s; dy = c; break;
                    default: // Top-Left: startAngle = PI -> cos(t + PI) = -cos(t), sin(t + PI) = -sin(t)
                        dx = -c; dy = -s; break;
                }
                context.LineTo(new Point(center.X + radius * dx, center.Y + radius * dy), false, false);
            }
            context.LineTo(end, false, false);
        }
        else
            context.LineTo(end, false, false);
    }
}
