using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public interface IColorExtractionService
{
    Color ExtractDominantColor(BitmapImage? image);
}

public sealed class ColorExtractionService : IColorExtractionService
{
    public Color ExtractDominantColor(BitmapImage? image)
    {
        if (image == null)
        {
            return Color.FromRgb(255, 255, 255);
        }

        try
        {
            const int sampleDimension = 64;
            var formatConvertedBitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

            double scaleX = (double)sampleDimension / formatConvertedBitmap.PixelWidth;
            double scaleY = (double)sampleDimension / formatConvertedBitmap.PixelHeight;
            var smallBitmap = new TransformedBitmap(formatConvertedBitmap, new ScaleTransform(scaleX, scaleY));

            int width = smallBitmap.PixelWidth;
            int height = smallBitmap.PixelHeight;
            int stride = width * 4;
            int bufLen = height * stride;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                smallBitmap.CopyPixels(pixels, stride, 0);

                var sampledColors = new List<Color>(300);
                var random = new Random(42);

                for (int i = 0; i < 300; i++)
                {
                    int x = random.Next(0, width);
                    int y = random.Next(0, height);
                    int index = (y * stride) + (x * 4);

                    byte b = pixels[index];
                    byte g = pixels[index + 1];
                    byte r = pixels[index + 2];
                    byte a = pixels[index + 3];

                    int brightness = (r + g + b) / 3;

                    bool isTooDark = brightness < 60;
                    bool isTooBright = brightness > 245;
                    bool isTransparent = a < 100;

                    if (!isTooDark && !isTooBright && !isTransparent)
                    {
                        sampledColors.Add(Color.FromArgb(a, r, g, b));
                    }
                }

                if (sampledColors.Count == 0)
                {
                    return Color.FromRgb(255, 255, 255);
                }

                var dominantColor = FindMostCommonColor(sampledColors);

                dominantColor = EnhanceSaturation(dominantColor, 1.3);
                dominantColor = EnsureMinimumBrightness(dominantColor, 100);

                return dominantColor;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch
        {
            return Color.FromRgb(255, 255, 255);
        }
    }

    private Color FindMostCommonColor(List<Color> colors)
    {
        const int tolerance = 50;
        var colorGroups = new Dictionary<string, List<Color>>();

        foreach (var color in colors)
        {
            int rBucket = (color.R / tolerance) * tolerance;
            int gBucket = (color.G / tolerance) * tolerance;
            int bBucket = (color.B / tolerance) * tolerance;

            string key = $"{rBucket},{gBucket},{bBucket}";

            if (!colorGroups.ContainsKey(key))
                colorGroups[key] = new List<Color>();

            colorGroups[key].Add(color);
        }

        var largestGroup = colorGroups.OrderByDescending(g => g.Value.Count).First().Value;

        int avgR = (int)largestGroup.Average(c => c.R);
        int avgG = (int)largestGroup.Average(c => c.G);
        int avgB = (int)largestGroup.Average(c => c.B);

        return Color.FromRgb((byte)avgR, (byte)avgG, (byte)avgB);
    }

    private Color EnhanceSaturation(Color color, double factor)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0, s = 0, v = max;

        if (delta > 0)
        {
            s = delta / max;

            if (r == max)
                h = (g - b) / delta + (g < b ? 6 : 0);
            else if (g == max)
                h = (b - r) / delta + 2;
            else
                h = (r - g) / delta + 4;

            h /= 6;
        }

        s = Math.Min(1.0, s * factor);

        double c = v * s;
        double x = c * (1 - Math.Abs((h * 6) % 2 - 1));
        double m = v - c;

        double rPrime = 0, gPrime = 0, bPrime = 0;

        if (h < 1.0 / 6)
        {
            rPrime = c; gPrime = x; bPrime = 0;
        }
        else if (h < 2.0 / 6)
        {
            rPrime = x; gPrime = c; bPrime = 0;
        }
        else if (h < 3.0 / 6)
        {
            rPrime = 0; gPrime = c; bPrime = x;
        }
        else if (h < 4.0 / 6)
        {
            rPrime = 0; gPrime = x; bPrime = c;
        }
        else if (h < 5.0 / 6)
        {
            rPrime = x; gPrime = 0; bPrime = c;
        }
        else
        {
            rPrime = c; gPrime = 0; bPrime = x;
        }

        return Color.FromArgb(
            color.A,
            (byte)Math.Round((rPrime + m) * 255),
            (byte)Math.Round((gPrime + m) * 255),
            (byte)Math.Round((bPrime + m) * 255)
        );
    }

    private Color EnsureMinimumBrightness(Color color, int minBrightness)
    {
        int currentBrightness = (color.R + color.G + color.B) / 3;

        if (currentBrightness >= minBrightness)
            return color;

        double scale = minBrightness / (double)Math.Max(currentBrightness, 1);

        return Color.FromArgb(
            color.A,
            (byte)Math.Min(255, color.R * scale),
            (byte)Math.Min(255, color.G * scale),
            (byte)Math.Min(255, color.B * scale)
        );
    }
}
