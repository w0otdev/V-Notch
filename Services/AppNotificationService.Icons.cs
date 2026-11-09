using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public sealed partial class AppNotificationService
{
    private const int AppIconDecodeSize = 128;

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
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (pixels[y * stride + x * 4 + 3] <= 8) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            if (right < left) return null;

            // AppDisplayInfo can return a tile with a small logo in its center.
            // Keep a little breathing room, rather than scaling the entire tile to 16 DIP.
            int padding = Math.Max(1, (int)Math.Ceiling(Math.Max(right - left + 1, bottom - top + 1) * .04));
            left = Math.Max(0, left - padding);
            top = Math.Max(0, top - padding);
            right = Math.Min(width - 1, right + padding);
            bottom = Math.Min(height - 1, bottom + padding);
            if (left == 0 && top == 0 && right == width - 1 && bottom == height - 1)
                return source;

            var cropped = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
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
