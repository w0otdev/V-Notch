using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class GlassGrainBrush
{
    private static ImageBrush? _cachedBrush;
    private static readonly object _sync = new();

    public static ImageBrush Instance
    {
        get
        {
            if (_cachedBrush != null) return _cachedBrush;
            lock (_sync)
            {
                if (_cachedBrush != null) return _cachedBrush;
                _cachedBrush = CreateBrush();
                return _cachedBrush;
            }
        }
    }

    private static ImageBrush CreateBrush()
    {
        const int size = 256;
        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        int[] pixels = new int[size * size];

        var rng = new Random(1337);
        for (int i = 0; i < pixels.Length; i++)
        {
            // Monochromatic noise: fine micro-dots with subtle contrast
            double n = rng.NextDouble() - 0.5;
            byte lum = n >= 0 ? (byte)255 : (byte)0;
            byte alpha = (byte)Math.Clamp(Math.Abs(n) * 110.0, 0, 255);

            pixels[i] = (alpha << 24) | (lum << 16) | (lum << 8) | lum;
        }

        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        wb.Freeze();

        var brush = new ImageBrush(wb)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }
}
