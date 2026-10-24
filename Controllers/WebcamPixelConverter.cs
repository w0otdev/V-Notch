using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace VNotch.Controllers;

internal static partial class WebcamPixelConverter
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_webcam_convert_rgb_in_place(
        Span<byte> pixels, int length, byte isRgba, int alphaMode);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_webcam_convert_gray8_to_bgra8(
        ReadOnlySpan<byte> src, int pixelCount, Span<byte> dst);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_webcam_convert_nv12_to_bgra8(
        ReadOnlySpan<byte> src, int width, int height, Span<byte> dst);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_webcam_convert_yuy2_to_bgra8(
        ReadOnlySpan<byte> src, int pixelCount, Span<byte> dst);

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
            using var converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            converted.CopyToBuffer(output.AsBuffer(0, requiredSize));
            return;
        }

        if (scratch.Length < inputSize)
            scratch = GC.AllocateUninitializedArray<byte>(inputSize, pinned: true);
        bitmap.CopyToBuffer(scratch.AsBuffer(0, inputSize));

        ReadOnlySpan<byte> srcSpan = scratch.AsSpan(0, inputSize);
        Span<byte> dstSpan = output.AsSpan(0, requiredSize);
        if (format == BitmapPixelFormat.Gray8)
        {
            _ = vn_webcam_convert_gray8_to_bgra8(srcSpan, pixels, dstSpan);
        }
        else if (format == BitmapPixelFormat.Nv12)
        {
            _ = vn_webcam_convert_nv12_to_bgra8(srcSpan, width, height, dstSpan);
        }
        else
        {
            _ = vn_webcam_convert_yuy2_to_bgra8(srcSpan, pixels, dstSpan);
        }
    }

    private static void ConvertRgb(Span<byte> pixels, bool rgba, BitmapAlphaMode alphaMode)
    {
        if (!rgba && alphaMode == BitmapAlphaMode.Premultiplied) return;
        _ = vn_webcam_convert_rgb_in_place(pixels, pixels.Length, rgba ? (byte)1 : (byte)0, (int)alphaMode);
    }
}
