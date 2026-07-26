using System;
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
                byte[] pixels = new byte[height * stride];
                smallBitmap.CopyPixels(pixels, stride, 0);

                byte[] target = new byte[pixels.Length];

                int passes = 2;
                for (int i = 0; i < passes; i++)
                {
                    BoxBlurHorizontal(pixels, target, width, height, blurRadius);
                    BoxBlurVertical(target, pixels, width, height, blurRadius);
                }

                DarkenPixels(pixels, 0.96f);

                var writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                writeableBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                writeableBitmap.Freeze();

                return (BitmapSource)writeableBitmap;
            }
            catch
            {
                return null;
            }
        });
    }

    private static void DarkenPixels(byte[] pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i += 4)
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
                int nx = dx < 0 ? 0 : (dx >= w ? w - 1 : dx);
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

                int outX = x - radius;
                outX = outX < 0 ? 0 : (outX >= w ? w - 1 : outX);
                int inX = x + 1 + radius;
                inX = inX < 0 ? 0 : (inX >= w ? w - 1 : inX);

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
                int ny = dy < 0 ? 0 : (dy >= h ? h - 1 : dy);
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

                int outY = y - radius;
                outY = outY < 0 ? 0 : (outY >= h ? h - 1 : outY);
                int inY = y + 1 + radius;
                inY = inY < 0 ? 0 : (inY >= h ? h - 1 : inY);

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
