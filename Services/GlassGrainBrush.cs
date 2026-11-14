using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static partial class GlassGrainBrush
{
    private static ImageBrush? _cachedBrush;
    private static readonly object _sync = new();

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_glass_generate_grain_pbgra32(
        Span<int> outPixels,
        int pixelCount,
        uint seed);

    internal static void GenerateGrainPixels(Span<int> pixels, uint seed = 1337u)
    {
        if (!pixels.IsEmpty)
            _ = vn_glass_generate_grain_pbgra32(pixels, pixels.Length, seed);
    }

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
        int[] pixels = GC.AllocateUninitializedArray<int>(size * size);
        GenerateGrainPixels(pixels, 1337u);

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
