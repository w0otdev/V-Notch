using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static partial class ArtworkFrameDetector
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_artwork_frame_detect_any(
        byte* pixels, int w, int h, int width, int height,
        out int outX, out int outY, out int outW, out int outH, out byte outFound);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_artwork_frame_detect_dark_textured(
        byte* pixels, int w, int h, int width, int height,
        out int outX, out int outY, out int outW, out int outH, out byte outFound);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_artwork_frame_detect_white(
        byte* pixels, int w, int h, int width, int height,
        out int outX, out int outY, out int outW, out int outH, out byte outFound);

    internal static unsafe Int32Rect? DetectFrame(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        if (Math.Min(width, height) < 64) return null;
        var (pixels, w, h) = RentDownscaledPixels(source, width, height);
        try
        {
            fixed (byte* p = pixels)
            {
                _ = vn_artwork_frame_detect_any(p, w, h, width, height, out int rx, out int ry, out int rw, out int rh, out byte found);
                return found != 0 ? new Int32Rect(rx, ry, rw, rh) : null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    internal static unsafe Int32Rect? DetectDarkTexturedFrame(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        if (Math.Min(width, height) < 64) return null;
        var (pixels, w, h) = RentDownscaledPixels(source, width, height);
        try
        {
            fixed (byte* p = pixels)
            {
                _ = vn_artwork_frame_detect_dark_textured(p, w, h, width, height, out int rx, out int ry, out int rw, out int rh, out byte found);
                return found != 0 ? new Int32Rect(rx, ry, rw, rh) : null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    internal static unsafe Int32Rect? DetectWhiteFrame(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        if (Math.Min(width, height) < 64) return null;
        var (pixels, w, h) = RentDownscaledPixels(source, width, height);
        try
        {
            fixed (byte* p = pixels)
            {
                _ = vn_artwork_frame_detect_white(p, w, h, width, height, out int rx, out int ry, out int rw, out int rh, out byte found);
                return found != 0 ? new Int32Rect(rx, ry, rw, rh) : null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static (byte[] Pixels, int W, int H) RentDownscaledPixels(BitmapSource source, int width, int height)
    {
        double scale = Math.Min(1, 384.0 / Math.Max(width, height));
        BitmapSource sample = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int w = sample.PixelWidth, h = sample.PixelHeight;
        int stride = w * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(h * stride);
        sample.CopyPixels(pixels, stride, 0);
        return (pixels, w, h);
    }
}
