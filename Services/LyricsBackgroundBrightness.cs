using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class LyricsBackgroundBrightness
{
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> Cache = new();

    internal static ImageSource? Apply(ImageSource? source, bool enabled) =>
        enabled && source is BitmapSource bitmap ? Cache.GetValue(bitmap, Brighten) : source;

    private static BitmapSource Brighten(BitmapSource source)
    {
        double scale = Math.Min(1, 512.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sample = scale < 1
            ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        double luminance = 0, weight = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double alpha = pixels[i + 3] / 255.0;
            luminance += (.0722 * pixels[i] + .7152 * pixels[i + 1] + .2126 * pixels[i + 2]) * alpha;
            weight += alpha;
        }
        if (weight == 0 || luminance / weight >= 150) return source;

        // Aim for visible midtones even under the lyrics opacity mask. Limit the
        // lift to retain contrast in very dark artwork; black and white stay fixed.
        double average = Math.Max(1, luminance / weight);
        double gamma = Math.Clamp(Math.Log(150.0 / 255) / Math.Log(average / 255), .45, 1);
        byte[] lookup = new byte[256];
        for (int i = 0; i < lookup.Length; i++)
            lookup[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, gamma));
        for (int i = 0; i < pixels.Length; i += 4)
            for (int channel = 0; channel < 3; channel++) pixels[i + channel] = lookup[pixels[i + channel]];
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }
}
