using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static partial class LyricsBackgroundBrightness
{
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> Cache = new();

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_lyrics_brightness_apply(byte* pixels, int byteCount, out byte outModified);

    internal static ImageSource? Apply(ImageSource? source, bool enabled) =>
        enabled && source is BitmapSource bitmap ? Cache.GetValue(bitmap, Brighten) : source;

    private static unsafe BitmapSource Brighten(BitmapSource source)
    {
        double scale = Math.Min(1, 512.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sample = scale < 1
            ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        int stride = width * 4;
        int byteCount = height * stride;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            fixed (byte* p = pixels)
            {
                _ = vn_lyrics_brightness_apply(p, byteCount, out byte modified);
                if (modified == 0)
                    return source;
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
