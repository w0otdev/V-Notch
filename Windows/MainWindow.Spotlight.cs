using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch;

public partial class MainWindow : ISpotlightMorphHost
{
    internal ImageSource? CaptureSpotlightMorphVisual()
    {
        double width = NotchBorder.ActualWidth;
        double height = NotchBorder.ActualHeight;
        if (width <= 0 || height <= 0) return null;

        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(NotchBorder);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            // Render via VisualBrush to neutralize parent layout offsets and keep
            // snapshots pixel-aligned with the notch across all views.
            var neutral = new DrawingVisual();
            using (DrawingContext ctx = neutral.RenderOpen())
            {
                var brush = new VisualBrush(NotchBorder) { Stretch = Stretch.None };
                ctx.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }
            bitmap.Render(neutral);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Services.RuntimeLog.Error(
                "SPOTLIGHT-MORPH", ex, "Could not capture notch handoff");
            return null;
        }
    }

    (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius)
        ISpotlightMorphHost.GetSpotlightMorphRect() => GetSpotlightMorphRect();

    ImageSource? ISpotlightMorphHost.CaptureSpotlightMorphVisual() =>
        CaptureSpotlightMorphVisual();

    void ISpotlightMorphHost.SetSpotlightMorphSessionActive(bool active) =>
        SetSpotlightMorphSessionActive(active);

    void ISpotlightMorphHost.SetSpotlightMorphActive(bool active) =>
        SetSpotlightMorphActive(active);

    void ISpotlightMorphHost.BeginSpotlightReturnHandoff(TimeSpan duration) =>
        BeginSpotlightReturnHandoff(duration);

    ImageSource? ISpotlightMorphHost.GetGlassBackdropImageSource() =>
        GlassBackdropImage?.Source;
}
