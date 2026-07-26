using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class SubjectAwareBlurService
{
    public static async Task<BitmapSource?> GetSubjectBlurredAsync(
        BitmapSource source,
        SubjectBounds? subject,
        int downscaleWidth = 192,
        int backgroundBlurRadius = 16,
        int subjectBlurRadius = 6)
    {
        if (source == null) return null;

        if (subject is not SubjectBounds s)
        {
            return await FastBlurService.GetBlurredImageAsync(source, downscaleWidth, backgroundBlurRadius);
        }

        return await Task.Run(() =>
        {
            try
            {
                int width = Math.Max(64, downscaleWidth);
                int height = (int)(source.PixelHeight * ((double)width / source.PixelWidth));
                if (height < 1) height = 1;
                backgroundBlurRadius = Math.Clamp(backgroundBlurRadius, 1, 32);
                subjectBlurRadius = Math.Clamp(subjectBlurRadius, 0, backgroundBlurRadius);

                var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var small = new TransformedBitmap(formatted,
                    new ScaleTransform((double)width / formatted.PixelWidth, (double)height / formatted.PixelHeight));

                int stride = width * 4;
                int bufLen = height * stride;

                byte[] basePixels = new byte[bufLen];
                small.CopyPixels(basePixels, stride, 0);

                byte[] background = (byte[])basePixels.Clone();
                byte[] tmp = new byte[bufLen];
                for (int i = 0; i < 2; i++)
                {
                    BoxBlurHorizontal(background, tmp, width, height, backgroundBlurRadius);
                    BoxBlurVertical(tmp, background, width, height, backgroundBlurRadius);
                }
                Darken(background, 0.78f);

                byte[] subjectLayer = (byte[])basePixels.Clone();
                if (subjectBlurRadius >= 1)
                {
                    BoxBlurHorizontal(subjectLayer, tmp, width, height, subjectBlurRadius);
                    BoxBlurVertical(tmp, subjectLayer, width, height, subjectBlurRadius);
                }
                Brighten(subjectLayer, 1.04f);

                float cx = Math.Clamp(s.CenterX, 0f, 1f) * width;
                float cy = Math.Clamp(s.CenterY, 0f, 1f) * height;
                float rx = MathF.Max(width * 0.18f, s.Width * width * 0.55f);
                float ry = MathF.Max(height * 0.22f, s.Height * height * 0.55f);
                float feather = 0.40f;

                byte[] result = new byte[bufLen];
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    float dy = (y - cy) / ry;
                    for (int x = 0; x < width; x++)
                    {
                        float dx = (x - cx) / rx;
                        float distSq = dx * dx + dy * dy;
                        float dist = MathF.Sqrt(distSq);

                        float t;
                        if (dist <= 1f)
                        {
                            t = 1f;
                        }
                        else if (dist >= 1f + feather)
                        {
                            t = 0f;
                        }
                        else
                        {
                            float u = (dist - 1f) / feather;
                            t = 1f - u * u * (3f - 2f * u);
                        }

                        int p = row + x * 4;
                        byte bb = background[p];
                        byte gb = background[p + 1];
                        byte rb = background[p + 2];
                        byte bs = subjectLayer[p];
                        byte gs = subjectLayer[p + 1];
                        byte rs = subjectLayer[p + 2];

                        result[p] = (byte)(bb + (bs - bb) * t);
                        result[p + 1] = (byte)(gb + (gs - gb) * t);
                        result[p + 2] = (byte)(rb + (rs - rb) * t);
                        result[p + 3] = 255;
                    }
                }

                var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), result, stride, 0);
                wb.Freeze();
                return (BitmapSource)wb;
            }
            catch
            {
                return null;
            }
        });
    }

    private static void Darken(byte[] pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * factor);
            pixels[i + 1] = (byte)(pixels[i + 1] * factor);
            pixels[i + 2] = (byte)(pixels[i + 2] * factor);
        }
    }

    private static void Brighten(byte[] pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)Math.Min(255, pixels[i] * factor);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * factor);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * factor);
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
