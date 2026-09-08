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
#pragma warning disable S2245 // Pseudo-random generator is used solely for deterministic pixel sampling of images, not security or cryptography
                var random = new Random(42);
#pragma warning restore S2245

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
        catch (Exception)
        {
            return Color.FromRgb(255, 255, 255);
        }
    }

    private struct ColorBucket
    {
        public int Count;
        public long SumR;
        public long SumG;
        public long SumB;
    }

    private static Color FindMostCommonColor(List<Color> colors)
    {
        if (colors == null || colors.Count == 0)
            return Color.FromRgb(255, 255, 255);

        const int tolerance = 50;
        var colorGroups = new Dictionary<int, ColorBucket>();

        foreach (var color in colors)
        {
            int rBucket = (color.R / tolerance) * tolerance;
            int gBucket = (color.G / tolerance) * tolerance;
            int bBucket = (color.B / tolerance) * tolerance;

            int key = (rBucket << 16) | (gBucket << 8) | bBucket;

            if (colorGroups.TryGetValue(key, out var bucket))
            {
                bucket.Count++;
                bucket.SumR += color.R;
                bucket.SumG += color.G;
                bucket.SumB += color.B;
                colorGroups[key] = bucket;
            }
            else
            {
                colorGroups[key] = new ColorBucket
                {
                    Count = 1,
                    SumR = color.R,
                    SumG = color.G,
                    SumB = color.B
                };
            }
        }

        ColorBucket bestBucket = default;
        int maxCount = -1;

        foreach (var bucket in colorGroups.Values)
        {
            if (bucket.Count > maxCount)
            {
                maxCount = bucket.Count;
                bestBucket = bucket;
            }
        }

        if (bestBucket.Count <= 0)
            return Color.FromRgb(255, 255, 255);

        int avgR = (int)(bestBucket.SumR / bestBucket.Count);
        int avgG = (int)(bestBucket.SumG / bestBucket.Count);
        int avgB = (int)(bestBucket.SumB / bestBucket.Count);

        return Color.FromRgb((byte)avgR, (byte)avgG, (byte)avgB);
    }

    private static Color EnhanceSaturation(Color color, double factor)
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

            if (Math.Abs(r - max) < 0.0001)
                h = (g - b) / delta + (g < b ? 6 : 0);
            else if (Math.Abs(g - max) < 0.0001)
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

    private static Color EnsureMinimumBrightness(Color color, int minBrightness)
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
