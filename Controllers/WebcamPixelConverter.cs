using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace VNotch.Controllers;

internal static class WebcamPixelConverter
{
    internal static void CopyToBgra8(SoftwareBitmap bitmap, byte[] output, int requiredSize, ref byte[] scratch)
    {
        BitmapPixelFormat format = bitmap.BitmapPixelFormat;
        if (format is BitmapPixelFormat.Bgra8 or BitmapPixelFormat.Rgba8)
        {
            bitmap.CopyToBuffer(output.AsBuffer(0, requiredSize));
            ConvertRgb(output.AsSpan(0, requiredSize), format == BitmapPixelFormat.Rgba8, bitmap.BitmapAlphaMode);
            return;
        }

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int pixels = checked(width * height);
        int inputSize = format switch
        {
            BitmapPixelFormat.Gray8 => pixels,
            BitmapPixelFormat.Nv12 when width % 2 == 0 && height % 2 == 0 => checked(pixels + pixels / 2),
            BitmapPixelFormat.Yuy2 when width % 2 == 0 => checked(pixels * 2),
            _ => 0
        };
        if (inputSize == 0)
        {
            // Rare formats keep the platform converter as a compatibility fallback.
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            converted.CopyToBuffer(output.AsBuffer(0, requiredSize));
            return;
        }

        if (scratch.Length < inputSize)
            scratch = GC.AllocateUninitializedArray<byte>(inputSize, pinned: true);
        bitmap.CopyToBuffer(scratch.AsBuffer(0, inputSize));
        if (format == BitmapPixelFormat.Gray8)
        {
            for (int i = 0; i < pixels; i++)
            {
                int offset = i * 4;
                output[offset] = output[offset + 1] = output[offset + 2] = scratch[i];
                output[offset + 3] = 255;
            }
        }
        else if (format == BitmapPixelFormat.Nv12)
        {
            for (int y = 0; y < height; y++)
            {
                int uvRow = pixels + y / 2 * width;
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    int uv = uvRow + (x & ~1);
                    WriteYuv(output, index * 4, scratch[index], scratch[uv], scratch[uv + 1]);
                }
            }
        }
        else
        {
            for (int i = 0; i < pixels; i += 2)
            {
                int offset = i * 2;
                WriteYuv(output, i * 4, scratch[offset], scratch[offset + 1], scratch[offset + 3]);
                WriteYuv(output, (i + 1) * 4, scratch[offset + 2], scratch[offset + 1], scratch[offset + 3]);
            }
        }
    }

    private static void ConvertRgb(Span<byte> pixels, bool rgba, BitmapAlphaMode alphaMode)
    {
        if (!rgba && alphaMode == BitmapAlphaMode.Premultiplied) return;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (rgba) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            if (alphaMode == BitmapAlphaMode.Ignore)
                pixels[i + 3] = 255;
            else if (alphaMode == BitmapAlphaMode.Straight)
            {
                int alpha = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
            }
        }
    }

    // BT.601 studio-range YUV used by SoftwareBitmap's NV12/YUY2 camera formats.
    private static void WriteYuv(byte[] output, int offset, int y, int u, int v)
    {
        int luminance = 298 * (y - 16);
        int blueDifference = u - 128;
        int redDifference = v - 128;
        output[offset] = (byte)Math.Clamp((luminance + 516 * blueDifference + 128) >> 8, 0, 255);
        output[offset + 1] = (byte)Math.Clamp((luminance - 100 * blueDifference - 208 * redDifference + 128) >> 8, 0, 255);
        output[offset + 2] = (byte)Math.Clamp((luminance + 409 * redDifference + 128) >> 8, 0, 255);
        output[offset + 3] = 255;
    }
}
