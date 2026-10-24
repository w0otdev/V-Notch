using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static partial class SubjectAwareBlurService
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_subject_blur_brighten(byte* pixels, int bufLen, float factor);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_subject_blur_composite(
        byte* background,
        byte* subjectLayer,
        float centerX,
        float centerY,
        float subWidth,
        float subHeight,
        int width,
        int height,
        int stride);

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

            int actualW = small.PixelWidth;
            int actualH = small.PixelHeight;
            int stride = actualW * 4;
            int bufLen = actualH * stride;

            byte[] background = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] subjectLayer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);
            byte[] tmp = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                small.CopyPixels(background, stride, 0);
                Buffer.BlockCopy(background, 0, subjectLayer, 0, bufLen);

                RenderBackgroundLayer(background, tmp, actualW, actualH, backgroundBlurRadius);
                RenderSubjectLayer(subjectLayer, tmp, actualW, actualH, bufLen, subjectBlurRadius);
                CompositeLayers(background, subjectLayer, s, actualW, actualH, stride);

                var wb = new WriteableBitmap(actualW, actualH, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, actualW, actualH), background, stride, 0);
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
        int backgroundBlurRadius)
    {
        for (int i = 0; i < 2; i++)
        {
            FastBlurService.BoxBlurHorizontal(background, tmp, width, height, backgroundBlurRadius);
            FastBlurService.BoxBlurVertical(tmp, background, width, height, backgroundBlurRadius);
        }
        FastBlurService.DarkenPixels(background, width, height, 0.78f);
    }

    private static unsafe void RenderSubjectLayer(
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
        fixed (byte* p = subjectLayer)
        {
            _ = vn_subject_blur_brighten(p, Math.Min(subjectLayer.Length, bufLen), 1.04f);
        }
    }

    private static unsafe void CompositeLayers(
        byte[] background,
        byte[] subjectLayer,
        SubjectBounds s,
        int width,
        int height,
        int stride)
    {
        fixed (byte* pBg = background)
        fixed (byte* pSub = subjectLayer)
        {
            _ = vn_subject_blur_composite(pBg, pSub, s.CenterX, s.CenterY, s.Width, s.Height, width, height, stride);
        }
    }
}
