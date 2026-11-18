using System.Windows;
using System.Windows.Media;

namespace VNotch;

public partial class MainWindow : ISpotlightMorphHost
{
    internal ImageSource? CaptureSpotlightMorphVisual()
    {
        double width = NotchBorder.ActualWidth;
        double height = NotchBorder.ActualHeight;
        if (width <= 0 || height <= 0) return null;

        // Spotlight owns the brush only during the handoff. Reference the
        // notch below its hidden wrapper so composition can draw it directly.
        var bounds = new Rect(0, 0, width, height);
        var brush = new VisualBrush(NotchBorder)
        {
            AutoLayoutContent = false,
            Stretch = Stretch.None
        };
        return new DrawingImage(new GeometryDrawing(brush, null, new RectangleGeometry(bounds)));
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
}
