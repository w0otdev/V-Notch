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

        try
        {
            source = await ArtworkAnalysisSource.GetFrozenSnapshotAsync(source).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SUBJECT-BLUR", $"Could not prepare bitmap: {ex}");
            return null;
        }

        return await Task.Run(() => ProcessSubjectBlur(source, s, downscaleWidth, backgroundBlurRadius, subjectBlurRadius)).ConfigureAwait(false);
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

            var formatted = ArtworkAnalysisSource.GetBgra32(source);
            var small = new TransformedBitmap(formatted,
                new ScaleTransform((double)width / formatted.PixelWidth, (double)height / formatted.PixelHeight));

            int stride = width * 4;
            int bufLen = height * stride;

            byte[] background = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] subjectLayer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                small.CopyPixels(background, stride, 0);
                Buffer.BlockCopy(background, 0, subjectLayer, 0, bufLen);

                RenderBackgroundLayer(background, tmp, width, height, bufLen, backgroundBlurRadius);
                RenderSubjectLayer(subjectLayer, tmp, width, height, bufLen, subjectBlurRadius);
                CompositeLayers(background, subjectLayer, s, width, height, stride);

                var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), background, stride, 0);
                wb.Freeze();
                return wb;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(background);
                System.Buffers.ArrayPool<byte>.Shared.Return(subjectLayer);
                System.Buffers.ArrayPool<byte>.Shared.Return(tmp);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SUBJECT-BLUR", $"Image processing failed: {ex}");
            return null;
        }
    }

    private static void RenderBackgroundLayer(
        byte[] background,
        byte[] tmp,
        int width,
        int height,
        int bufLen,
        int backgroundBlurRadius)
    {
        for (int i = 0; i < 2; i++)
        {
            FastBlurService.BoxBlurHorizontal(background, tmp, width, height, backgroundBlurRadius);
            FastBlurService.BoxBlurVertical(tmp, background, width, height, backgroundBlurRadius);
        }
        Darken(background, bufLen, 0.78f);
    }

    private static void RenderSubjectLayer(
        byte[] subjectLayer,
        byte[] tmp,
        int width,
        int height,
        int bufLen,
        int subjectBlurRadius)
    {
        if (subjectBlurRadius >= 1)
        {
            FastBlurService.BoxBlurHorizontal(subjectLayer, tmp, width, height, subjectBlurRadius);
            FastBlurService.BoxBlurVertical(tmp, subjectLayer, width, height, subjectBlurRadius);
        }
        Brighten(subjectLayer, bufLen, 1.04f);
    }

    private static void CompositeLayers(
        byte[] background,
        byte[] subjectLayer,
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
            float dySquared = dy * dy;
            for (int x = 0; x < width; x++)
            {
                float dx = (x - cx) / rx;
                float t = ComputeBlendWeight(dx * dx + dySquared, feather);

                int p = row + x * 4;
                byte bb = background[p];
                byte gb = background[p + 1];
                byte rb = background[p + 2];
                byte bs = subjectLayer[p];
                byte gs = subjectLayer[p + 1];
                byte rs = subjectLayer[p + 2];

                background[p] = (byte)(bb + (bs - bb) * t);
                background[p + 1] = (byte)(gb + (gs - gb) * t);
                background[p + 2] = (byte)(rb + (rs - rb) * t);
                background[p + 3] = 255;
            }
        }
    }

    private static float ComputeBlendWeight(float distanceSquared, float feather)
    {
        if (distanceSquared <= 1f)
            return 1f;
        if (distanceSquared >= (1f + feather) * (1f + feather))
            return 0f;

        float dist = MathF.Sqrt(distanceSquared);
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
}
