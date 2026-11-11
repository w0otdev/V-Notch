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
    private static unsafe partial int vn_subject_blur_process_bgra32(
        byte* background,
        byte* subjectLayer,
        byte* scratch,
        int w,
        int h,
        int stride,
        float centerX,
        float centerY,
        float subW,
        float subH,
        int bgRadius,
        int subRadius,
        float bgDarken,
        float subBrighten);

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
                unsafe
                {
                    fixed (byte* pBg = background)
                    fixed (byte* pSub = subjectLayer)
                    fixed (byte* pTmp = tmp)
                    {
                        _ = vn_subject_blur_process_bgra32(
                            pBg, pSub, pTmp,
                            actualW, actualH, stride,
                            s.CenterX, s.CenterY, s.Width, s.Height,
                            backgroundBlurRadius, subjectBlurRadius,
                            0.78f, 1.04f);
                    }
                }

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
}
