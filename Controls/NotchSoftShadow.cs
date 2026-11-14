using System;
using System.Windows;
using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>
/// The notch's drop shadow, painted with gradients. A DropShadowEffect blurs the silhouette again on every
/// frame the notch changes size; opening and closing spent about a quarter of each frame on that. This
/// paints the same falloff (a Gaussian with sigma = BlurRadius / 3, shifted down by Depth) as four edge
/// bands and four corner gradients, which cost next to nothing at any size. Along the edges it matches the
/// blur to a colour step; out of a rounded corner it is up to about 5 % darker, which a radial gradient
/// cannot avoid.
/// </summary>
internal sealed class NotchSoftShadow : FrameworkElement
{
    // The values of the NotchShadow DropShadowEffect this stands in for.
    internal const double BlurRadius = 20;
    internal const double Depth = 4;
    internal const double Strength = 0.6;

    private const double Sigma = BlurRadius / 3;
    private const double Reach = 3 * Sigma;
    // Bands reach this far under the silhouette, past the part the notch itself hides.
    private const double Inset = Depth + 1;
    private const int Stops = 16;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(NotchSoftShadow),
        new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush LeftBand = Band(new Point(0, 0.5), new Point(1, 0.5));
    private static readonly Brush RightBand = Band(new Point(1, 0.5), new Point(0, 0.5));
    private static readonly Brush TopBand = Band(new Point(0.5, 0), new Point(0.5, 1));
    private static readonly Brush BottomBand = Band(new Point(0.5, 1), new Point(0.5, 0));

    public NotchSoftShadow()
    {
        IsHitTestVisible = false;
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Shadow opacity at a signed distance from the shifted silhouette's edge (negative = inside).</summary>
    internal static double AlphaAt(double distance) => Strength * (1 - NormalCdf(distance / Sigma));

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        double limit = Math.Min(w, h) / 2;
        double tl = Math.Clamp(CornerRadius.TopLeft, 0, limit), tr = Math.Clamp(CornerRadius.TopRight, 0, limit);
        double br = Math.Clamp(CornerRadius.BottomRight, 0, limit), bl = Math.Clamp(CornerRadius.BottomLeft, 0, limit);
        double top = Depth, bottom = h + Depth;
        double band = Reach + Inset;

        dc.DrawRectangle(LeftBand, null, Span(-Reach, top + tl, band, bottom - bl - (top + tl)));
        dc.DrawRectangle(RightBand, null, Span(w - Inset, top + tr, band, bottom - br - (top + tr)));
        dc.DrawRectangle(TopBand, null, Span(tl, top - Reach, w - tl - tr, band));
        dc.DrawRectangle(BottomBand, null, Span(bl, bottom - Inset, w - bl - br, band));

        Corner(dc, new Point(tl, top + tl), tl, Span(-Reach, top - Reach, tl + Reach, tl + Reach));
        Corner(dc, new Point(w - tr, top + tr), tr, Span(w - tr, top - Reach, tr + Reach, tr + Reach));
        Corner(dc, new Point(w - br, bottom - br), br, Span(w - br, bottom - br, br + Reach, br + Reach));
        Corner(dc, new Point(bl, bottom - bl), bl, Span(-Reach, bottom - bl, bl + Reach, bl + Reach));
    }

    private static Rect Span(double x, double y, double width, double height) =>
        new(x, y, Math.Max(0, width), Math.Max(0, height));

    private static void Corner(DrawingContext dc, Point centre, double radius, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        double outer = radius + Reach;
        var stops = new GradientStopCollection(Stops + 1);
        for (int i = 0; i <= Stops; i++)
        {
            double offset = (double)i / Stops;
            stops.Add(new GradientStop(Shade(AlphaAt(offset * outer - radius)), offset));
        }
        var brush = new RadialGradientBrush(stops)
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = centre,
            GradientOrigin = centre,
            RadiusX = outer,
            RadiusY = outer,
        };
        brush.Freeze();
        dc.DrawRectangle(brush, null, area);
    }

    /// <summary>A gradient across a band, from its outer edge (Reach outside) to Inset under the silhouette.</summary>
    private static Brush Band(Point outer, Point inner)
    {
        var stops = new GradientStopCollection(Stops + 1);
        for (int i = 0; i <= Stops; i++)
        {
            double offset = (double)i / Stops;
            stops.Add(new GradientStop(Shade(AlphaAt(Reach - offset * (Reach + Inset))), offset));
        }
        var brush = new LinearGradientBrush(stops, outer, inner);
        brush.Freeze();
        return brush;
    }

    private static Color Shade(double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 0, 0, 0);

    // Abramowitz-Stegun 7.1.26; error below 1.5e-7, far under one colour step.
    private static double NormalCdf(double x)
    {
        double z = Math.Abs(x) / Math.Sqrt(2);
        double t = 1 / (1 + 0.3275911 * z);
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return 0.5 * (1 + Math.Sign(x) * erf);
    }
}
