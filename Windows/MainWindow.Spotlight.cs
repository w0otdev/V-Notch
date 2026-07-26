using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch;

public partial class MainWindow
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
            bitmap.Render(NotchBorder);
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
}
