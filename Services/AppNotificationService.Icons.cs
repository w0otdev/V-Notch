using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public sealed partial class AppNotificationService
{
    private const int AppIconDecodeSize = 128;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnIconCropRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_compute_icon_crop_bounds(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        byte alphaThreshold,
        out VnIconCropRect outCrop,
        out byte outHasOpaque,
        out byte outShouldCrop);

    internal static bool TryComputeIconCropBounds(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        out Int32Rect cropRect,
        out bool shouldCrop)
    {
        cropRect = default;
        shouldCrop = false;
        if (vn_appnotify_compute_icon_crop_bounds(
                bgra, width, height, stride, 8,
                out var rect, out byte hasOpaque, out byte nativeShouldCrop) != 0 ||
            hasOpaque == 0)
        {
            return false;
        }

        cropRect = new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height);
        shouldCrop = nativeShouldCrop != 0;
        return true;
    }

    internal static ImageSource? NormalizeAppIcon(ImageSource? icon)
    {
        if (icon is not BitmapSource bitmap) return icon;
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;

        BitmapSource source = bitmap;
        if (!source.IsFrozen)
        {
            source = source.CloneCurrentValue();
            source.Freeze();
        }
        double scale = Math.Min(1.0, 256.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1.0)
        {
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            source.Freeze();
        }

        BitmapSource sample = source;
        if (sample.Format != PixelFormats.Bgra32 && sample.Format != PixelFormats.Pbgra32)
        {
            sample = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            sample.Freeze();
        }

        int width = sample.PixelWidth, height = sample.PixelHeight;
        int stride = width * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(stride * height);
        try
        {
            sample.CopyPixels(pixels, stride, 0);
            if (!TryComputeIconCropBounds(
                    pixels.AsSpan(0, stride * height),
                    width,
                    height,
                    stride,
                    out var cropRect,
                    out bool shouldCrop))
            {
                return null;
            }

            if (!shouldCrop)
                return source;

            var cropped = new CroppedBitmap(source, cropRect);
            cropped.Freeze();
            return cropped;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static ImageSource? TryLoadAppIconSource(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeAppIcon(FileIconProvider.GetAppIcon(path, small: false)
                ?? FileIconProvider.GetFileIcon(path));
        }

        try
        {
            // IconUri may point to PNG/ICO artwork. Shell file icons describe its
            // file type, so decode the artwork itself instead.
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = AppIconDecodeSize;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return NormalizeAppIcon(bitmap);
        }
        catch
        {
            return null;
        }
    }
}
