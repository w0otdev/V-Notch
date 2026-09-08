using System;
using System.Buffers;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class FastBlurService
{

    public static async Task<BitmapSource?> GetBlurredImageAsync(BitmapSource source, int downscaleWidth = 128, int blurRadius = 8)
    {
        if (source == null) return null;

        return await Task.Run(() =>
        {
            try
            {
                int width = Math.Max(64, downscaleWidth);
                int height = (int)(source.PixelHeight * ((double)width / source.PixelWidth));
                if (height < 1) height = 1;
                blurRadius = Math.Clamp(blurRadius, 1, 20);

                var formattedBitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                var smallBitmap = new TransformedBitmap(formattedBitmap, new ScaleTransform((double)width / formattedBitmap.PixelWidth, (double)height / formattedBitmap.PixelHeight));

                int stride = width * 4;
                int bufferSize = height * stride;

                byte[] pixels = ArrayPool<byte>.Shared.Rent(bufferSize);
                byte[] target = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    smallBitmap.CopyPixels(pixels, stride, 0);

                    int passes = 2;
                    for (int i = 0; i < passes; i++)
                    {
                        BoxBlurHorizontal(pixels, target, width, height, blurRadius);
                        BoxBlurVertical(target, pixels, width, height, blurRadius);
                    }

                    DarkenPixels(pixels, bufferSize, 0.96f);

                    var writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    writeableBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                    writeableBitmap.Freeze();

                    return (BitmapSource)writeableBitmap;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    ArrayPool<byte>.Shared.Return(target);
                }
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    private static void DarkenPixels(byte[] pixels, int length, float factor)
    {
        for (int i = 0; i < length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * factor);
            pixels[i + 1] = (byte)(pixels[i + 1] * factor);
            pixels[i + 2] = (byte)(pixels[i + 2] * factor);
        }
    }

    private static void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        int window = 2 * radius + 1;

        for (int y = 0; y < h; y++)
        {
            int pBase = y * w * 4;

            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = Math.Clamp(dx, 0, w - 1);
                int o = pBase + nx * 4;
                sumB += source[o];
                sumG += source[o + 1];
                sumR += source[o + 2];
                sumA += source[o + 3];
            }

            for (int x = 0; x < w; x++)
            {
                int t = pBase + x * 4;
                target[t] = (byte)(sumB / window);
                target[t + 1] = (byte)(sumG / window);
                target[t + 2] = (byte)(sumR / window);
                target[t + 3] = (byte)(sumA / window);

                int outX = Math.Clamp(x - radius, 0, w - 1);
                int inX = Math.Clamp(x + 1 + radius, 0, w - 1);

                int oOut = pBase + outX * 4;
                int oIn = pBase + inX * 4;
                sumB += source[oIn] - source[oOut];
                sumG += source[oIn + 1] - source[oOut + 1];
                sumR += source[oIn + 2] - source[oOut + 2];
                sumA += source[oIn + 3] - source[oOut + 3];
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] target, int w, int h, int radius)
    {
        int window = 2 * radius + 1;
        int rowStride = w * 4;

        for (int x = 0; x < w; x++)
        {
            int col = x * 4;

            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = Math.Clamp(dy, 0, h - 1);
                int o = ny * rowStride + col;
                sumB += source[o];
                sumG += source[o + 1];
                sumR += source[o + 2];
                sumA += source[o + 3];
            }

            for (int y = 0; y < h; y++)
            {
                int t = y * rowStride + col;
                target[t] = (byte)(sumB / window);
                target[t + 1] = (byte)(sumG / window);
                target[t + 2] = (byte)(sumR / window);
                target[t + 3] = (byte)(sumA / window);

                int outY = Math.Clamp(y - radius, 0, h - 1);
                int inY = Math.Clamp(y + 1 + radius, 0, h - 1);

                int oOut = outY * rowStride + col;
                int oIn = inY * rowStride + col;
                sumB += source[oIn] - source[oOut];
                sumG += source[oIn + 1] - source[oOut + 1];
                sumR += source[oIn + 2] - source[oOut + 2];
                sumA += source[oIn + 3] - source[oOut + 3];
            }
        }
    }
}
