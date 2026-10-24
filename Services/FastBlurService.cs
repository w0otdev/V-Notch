using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static partial class FastBlurService
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_fast_blur_get_window_reciprocal(int window, out uint outReciprocal);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fast_blur_darken_pixels(byte* pixels, int length, float factor);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fast_blur_box_horizontal(byte* source, byte* target, int w, int h, int radius);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_fast_blur_box_vertical(byte* source, byte* target, int w, int h, int radius);

    public static async Task<BitmapSource?> GetBlurredImageAsync(BitmapSource source, int downscaleWidth = 128, int blurRadius = 8)
    {
        if (source == null) return null;

        try
        {
            source = await ArtworkAnalysisSource.GetFrozenSnapshotAsync(source).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("FAST-BLUR", $"Could not prepare bitmap: {ex}");
            return null;
        }

        return await Task.Run(() =>
        {
            try
            {
                int width = Math.Max(64, downscaleWidth);
                int height = (int)(source.PixelHeight * ((double)width / source.PixelWidth));
                if (height < 1) height = 1;
                blurRadius = Math.Clamp(blurRadius, 1, 20);

                var formattedBitmap = ArtworkAnalysisSource.GetBgra32(source);

                var smallBitmap = new TransformedBitmap(formattedBitmap, new ScaleTransform((double)width / formattedBitmap.PixelWidth, (double)height / formattedBitmap.PixelHeight));

                int actualW = smallBitmap.PixelWidth;
                int actualH = smallBitmap.PixelHeight;
                int stride = actualW * 4;
                int bufferSize = actualH * stride;

                byte[] pixels = ArrayPool<byte>.Shared.Rent(bufferSize);
                byte[] target = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    smallBitmap.CopyPixels(pixels, stride, 0);

                    int passes = 2;
                    for (int i = 0; i < passes; i++)
                    {
                        BoxBlurHorizontal(pixels, target, actualW, actualH, blurRadius);
                        BoxBlurVertical(target, pixels, actualW, actualH, blurRadius);
                    }

                    DarkenPixels(pixels, bufferSize);

                    var writeableBitmap = new WriteableBitmap(actualW, actualH, 96, 96, PixelFormats.Bgra32, null);
                    writeableBitmap.WritePixels(new Int32Rect(0, 0, actualW, actualH), pixels, stride, 0);
                    writeableBitmap.Freeze();

                    return (BitmapSource)writeableBitmap;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    ArrayPool<byte>.Shared.Return(target);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("FAST-BLUR", $"Image processing failed: {ex}");
                return null;
            }
        }).ConfigureAwait(false);
    }

    internal static void DarkenPixels(byte[] pixels, int width, int height, float factor)
        => DarkenPixels(pixels, width * height * 4, factor);

    internal static unsafe void DarkenPixels(byte[] pixels, int length, float factor = 0.96f)
    {
        if (pixels == null || length <= 0) return;
        fixed (byte* p = pixels)
        {
            _ = vn_fast_blur_darken_pixels(p, Math.Min(pixels.Length, length), factor);
        }
    }

    internal static unsafe void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        fixed (byte* pSrc = source)
        fixed (byte* pDst = target)
        {
            _ = vn_fast_blur_box_horizontal(pSrc, pDst, w, h, radius);
        }
    }

    internal static uint GetWindowReciprocal(int window)
    {
        _ = vn_fast_blur_get_window_reciprocal(window, out uint rec);
        return rec;
    }

    internal static unsafe void BoxBlurVertical(byte[] source, byte[] target, int w, int h, int radius)
    {
        fixed (byte* pSrc = source)
        fixed (byte* pDst = target)
        {
            _ = vn_fast_blur_box_vertical(pSrc, pDst, w, h, radius);
        }
    }
}
