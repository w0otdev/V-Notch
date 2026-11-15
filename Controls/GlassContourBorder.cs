using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

// Border's layout and brush API, with the same contour as the glass shader and
// material clip. Circular Border rendering otherwise leaves a second arc inside
// squircle corners, especially in the dark readability overlay.
public sealed class GlassContourBorder : Border
{
    public static readonly DependencyProperty PowerFactorProperty = DependencyProperty.RegisterAttached(
        nameof(PowerFactor), typeof(double), typeof(GlassContourBorder),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.Inherits |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public double PowerFactor
    {
        get => (double)GetValue(PowerFactorProperty);
        set => SetValue(PowerFactorProperty, value);
    }

    private Size _geometrySize;
    private CornerRadius _geometryRadius;
    private Thickness _geometryThickness;
    private double _geometryPower = double.NaN;
    private Geometry? _outline;
    private Geometry? _rim;

    protected override void OnRender(DrawingContext dc)
    {
        UpdateGeometry();
        if (_outline == null) return;
        if (Background != null) dc.DrawGeometry(Background, null, _outline);
        if (BorderBrush != null && _rim != null) dc.DrawGeometry(BorderBrush, null, _rim);
    }

    private void UpdateGeometry()
    {
        var size = RenderSize;
        var radius = CornerRadius;
        var thickness = BorderThickness;
        double power = double.IsFinite(PowerFactor) ? Math.Max(1.05, PowerFactor) : 2;
        if (_outline != null && size == _geometrySize && radius == _geometryRadius &&
            thickness == _geometryThickness && power == _geometryPower) return;

        _geometrySize = size;
        _geometryRadius = radius;
        _geometryThickness = thickness;
        _geometryPower = power;
        _outline = GlassClipBuilder.CreateClip(size, radius, power);
        _rim = null;
        if (_outline == null || thickness == new Thickness(0)) return;

        double innerWidth = size.Width - thickness.Left - thickness.Right;
        double innerHeight = size.Height - thickness.Top - thickness.Bottom;
        if (innerWidth <= 0 || innerHeight <= 0)
        {
            _rim = _outline;
            return;
        }
        // Clamp before insetting, just as the outer contour does on a short
        // notch. This keeps the ring concentric during the entrance morph.
        double limit = Math.Min(size.Width, size.Height) / 2;
        double Inset(double r, double x, double y) => Math.Max(0, Math.Min(r, limit) - (x + y) / 2);
        var innerRadius = new CornerRadius(
            Inset(radius.TopLeft, thickness.Left, thickness.Top),
            Inset(radius.TopRight, thickness.Right, thickness.Top),
            Inset(radius.BottomRight, thickness.Right, thickness.Bottom),
            Inset(radius.BottomLeft, thickness.Left, thickness.Bottom));
        var inner = GlassClipBuilder.CreateClip(new Size(innerWidth, innerHeight), innerRadius, power)!.Clone();
        inner.Transform = new TranslateTransform(thickness.Left, thickness.Top);
        inner.Freeze();
        _rim = new CombinedGeometry(GeometryCombineMode.Exclude, _outline, inner);
        _rim.Freeze();
    }
}
