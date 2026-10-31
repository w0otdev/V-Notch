using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static partial class ArtworkFrameDetector
{
    [ThreadStatic]
    internal static int RentDownscaledPixelsCallCount;

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
            return DetectFrameFromPixels(pixels, w, h, width, height);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    internal static unsafe Int32Rect? DetectFrameFromPixels(byte[] pixels, int w, int h, int width, int height)
    {
        fixed (byte* p = pixels)
        {
            _ = vn_artwork_frame_detect_any(p, w, h, width, height, out int rx, out int ry, out int rw, out int rh, out byte found);
            return found != 0 ? new Int32Rect(rx, ry, rw, rh) : null;
        }
    }

    internal static unsafe Int32Rect? DetectFrameFromPixels(byte[] pixels, int w, int h, int stride) =>
        DetectFrameFromPixels(pixels, w, h, w, h);

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

    internal static bool RentDownscaledPixels(
        BitmapSource source,
        out byte[]? pixels,
        out int w,
        out int h,
        out int stride,
        double maxDimension = 384.0)
    {
        if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            pixels = null;
            w = 0;
            h = 0;
            stride = 0;
            return false;
        }

        (pixels, w, h) = RentDownscaledPixels(source, source.PixelWidth, source.PixelHeight, maxDimension);
        stride = w * 4;
        return true;
    }

    internal static (byte[] Pixels, int W, int H) RentDownscaledPixels(
        BitmapSource source,
        int width,
        int height,
        double maxDimension = 384.0)
    {
        RentDownscaledPixelsCallCount++;
        double scale = Math.Min(1.0, maxDimension / Math.Max(width, height));
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();

        BitmapSource sample = scaled;
        if (scaled.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            sample = converted;
        }

        int w = sample.PixelWidth, h = sample.PixelHeight;
        int stride = w * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(h * stride);
        sample.CopyPixels(pixels, stride, 0);
        return (pixels, w, h);
    }
}
