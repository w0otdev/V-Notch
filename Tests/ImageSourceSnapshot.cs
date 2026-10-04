using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Tests;

internal static class ImageSourceSnapshot
{
    // RenderTargetBitmap is WPF's supported public snapshot API. D3DImage's
    // software fallback is enabled by the production presenter.
    internal static BitmapSource Capture(ImageSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, new Rect(0, 0, width, height));
        var snapshot = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(visual);
        snapshot.Freeze();
        return snapshot;
    }
}
