using System.Windows;
using System.Windows.Media;

namespace VNotch.Controllers;

// D3D keeps a stable capacity for animation. Clip its image before the shader
// input is rasterized, so a small idle capture keeps its original pixel density.
internal sealed class GlassCaptureImage
{
    private readonly RectangleGeometry _clip = new();
    internal ImageSource Source { get; }
    internal DrawingImage Image { get; }

    internal GlassCaptureImage(ImageSource source, int surfaceWidth, int surfaceHeight)
    {
        Source = source;
        var drawing = new DrawingGroup { ClipGeometry = _clip };
        drawing.Children.Add(new ImageDrawing(source, new Rect(0, 0, surfaceWidth, surfaceHeight)));
        Image = new DrawingImage(drawing);
    }

    internal void SetCaptureSize(int width, int height)
    {
        var rect = new Rect(0, 0, width, height);
        if (_clip.Rect != rect) _clip.Rect = rect;
    }
}
