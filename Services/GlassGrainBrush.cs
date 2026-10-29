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
        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        int[] pixels = new int[size * size];

#pragma warning disable S2245 // Pseudo-random generator is used solely for deterministic visual noise texture generation, not security or cryptography
        var rng = new Random(1337);
#pragma warning restore S2245
        for (int i = 0; i < pixels.Length; i++)
        {
            // Triangular zero-mean micro-grain distribution (avoids harsh binary clumping)
            double u1 = rng.NextDouble();
            double t = u1 - rng.NextDouble();
            byte alpha = (byte)(Math.Abs(t) * 55.0);
            // Premultiplied Pbgra32: white highlight grain uses (a, a, a, a), dark shadow grain uses (a, 0, 0, 0)
            pixels[i] = t >= 0
                ? (alpha << 24) | (alpha << 16) | (alpha << 8) | alpha
                : alpha << 24;
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
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        brush.Freeze();
        return brush;
    }
}
