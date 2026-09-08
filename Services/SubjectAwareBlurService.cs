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

        return await Task.Run(() => ProcessSubjectBlur(source, s, downscaleWidth, backgroundBlurRadius, subjectBlurRadius));
    }

    private static BitmapSource? ProcessSubjectBlur(
        BitmapSource source,
        SubjectBounds s,
        int downscaleWidth,
        int backgroundBlurRadius,
        int subjectBlurRadius)
    {
        try
        {
            int width = Math.Max(64, downscaleWidth);
            int height = Math.Max(1, (int)(source.PixelHeight * ((double)width / source.PixelWidth)));
            backgroundBlurRadius = Math.Clamp(backgroundBlurRadius, 1, 32);
            subjectBlurRadius = Math.Clamp(subjectBlurRadius, 0, backgroundBlurRadius);

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var small = new TransformedBitmap(formatted,
                new ScaleTransform((double)width / formatted.PixelWidth, (double)height / formatted.PixelHeight));

            int stride = width * 4;
            int bufLen = height * stride;

            byte[] basePixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] background = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] subjectLayer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] result = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                small.CopyPixels(basePixels, stride, 0);

                RenderBackgroundLayer(basePixels, background, tmp, width, height, bufLen, backgroundBlurRadius);
                RenderSubjectLayer(basePixels, subjectLayer, tmp, width, height, bufLen, subjectBlurRadius);
                CompositeLayers(background, subjectLayer, result, s, width, height, stride);

                var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), result, stride, 0);
                wb.Freeze();
                return wb;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(basePixels);
                System.Buffers.ArrayPool<byte>.Shared.Return(background);
                System.Buffers.ArrayPool<byte>.Shared.Return(subjectLayer);
                System.Buffers.ArrayPool<byte>.Shared.Return(tmp);
                System.Buffers.ArrayPool<byte>.Shared.Return(result);
            }
        }
        catch (Exception)
        {
            // Fall back to null if image transformation or rendering fails.
            return null;
        }
    }

    private static void RenderBackgroundLayer(
        byte[] basePixels,
        byte[] background,
        byte[] tmp,
        int width,
        int height,
        int bufLen,
        int backgroundBlurRadius)
    {
        Buffer.BlockCopy(basePixels, 0, background, 0, bufLen);
        for (int i = 0; i < 2; i++)
        {
            BoxBlurHorizontal(background, tmp, width, height, backgroundBlurRadius);
            BoxBlurVertical(tmp, background, width, height, backgroundBlurRadius);
        }
        Darken(background, bufLen, 0.78f);
    }

    private static void RenderSubjectLayer(
        byte[] basePixels,
        byte[] subjectLayer,
        byte[] tmp,
        int width,
        int height,
        int bufLen,
        int subjectBlurRadius)
    {
        Buffer.BlockCopy(basePixels, 0, subjectLayer, 0, bufLen);
        if (subjectBlurRadius >= 1)
        {
            BoxBlurHorizontal(subjectLayer, tmp, width, height, subjectBlurRadius);
            BoxBlurVertical(tmp, subjectLayer, width, height, subjectBlurRadius);
        }
        Brighten(subjectLayer, bufLen, 1.04f);
    }

    private static void CompositeLayers(
        byte[] background,
        byte[] subjectLayer,
        byte[] result,
        SubjectBounds s,
        int width,
        int height,
        int stride)
    {
        float cx = Math.Clamp(s.CenterX, 0f, 1f) * width;
        float cy = Math.Clamp(s.CenterY, 0f, 1f) * height;
        float rx = MathF.Max(width * 0.18f, s.Width * width * 0.55f);
        float ry = MathF.Max(height * 0.22f, s.Height * height * 0.55f);
        const float feather = 0.40f;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            float dy = (y - cy) / ry;
            for (int x = 0; x < width; x++)
            {
                float dx = (x - cx) / rx;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float t = ComputeBlendWeight(dist, feather);

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
    }

    private static float ComputeBlendWeight(float dist, float feather)
    {
        if (dist <= 1f)
            return 1f;
        if (dist >= 1f + feather)
            return 0f;

        float u = (dist - 1f) / feather;
        return 1f - u * u * (3f - 2f * u);
    }

    private static void Darken(byte[] pixels, int bufLen, float factor)
    {
        int limit = Math.Min(pixels.Length, bufLen);
        for (int i = 0; i < limit; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * factor);
            pixels[i + 1] = (byte)(pixels[i + 1] * factor);
            pixels[i + 2] = (byte)(pixels[i + 2] * factor);
        }
    }

    private static void Brighten(byte[] pixels, int bufLen, float factor)
    {
        int limit = Math.Min(pixels.Length, bufLen);
        for (int i = 0; i < limit; i += 4)
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
