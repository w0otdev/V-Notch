using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal readonly partial record struct ArtworkFingerprint(ulong ContentHash, int PixelWidth, int PixelHeight)
{
    private const int SampleEdge = 64;

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_artwork_fingerprint_compute(
        byte* pixels,
        int byteCount,
        int sourceWidth,
        int sourceHeight,
        int sampledWidth,
        int sampledHeight,
        out ulong outHash);

    public static unsafe ArtworkFingerprint Create(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int sourceWidth = source.PixelWidth;
        int sourceHeight = source.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new ArtworkFingerprint(0, sourceWidth, sourceHeight);

        BitmapSource sampled = source;
        double scale = Math.Min(1.0, (double)SampleEdge / Math.Max(sourceWidth, sourceHeight));
        if (scale < 1.0)
        {
            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            resized.Freeze();
            sampled = resized;
        }

        if (sampled.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            sampled = converted;
        }

        int stride = sampled.PixelWidth * 4;
        int byteCount = stride * sampled.PixelHeight;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            sampled.CopyPixels(pixels, stride, 0);
            fixed (byte* p = pixels)
            {
                _ = vn_artwork_fingerprint_compute(
                    p, byteCount, sourceWidth, sourceHeight, sampled.PixelWidth, sampled.PixelHeight, out ulong hash);
                return new ArtworkFingerprint(hash, sourceWidth, sourceHeight);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
