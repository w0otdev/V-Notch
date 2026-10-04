using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public interface IColorExtractionService
{
    Color ExtractDominantColor(BitmapSource? image);
}

public sealed class ColorExtractionService : IColorExtractionService
{
    public Color ExtractDominantColor(BitmapSource? image)
    {
        if (image == null)
        {
            return Color.FromRgb(255, 255, 255);
        }

        try
        {
            const int sampleDimension = 64;
            var formatConvertedBitmap = ArtworkAnalysisSource.GetBgra32(image);

            double scaleX = (double)sampleDimension / formatConvertedBitmap.PixelWidth;
            double scaleY = (double)sampleDimension / formatConvertedBitmap.PixelHeight;
            BitmapSource smallBitmap = formatConvertedBitmap.PixelWidth == sampleDimension && formatConvertedBitmap.PixelHeight == sampleDimension
                ? formatConvertedBitmap
                : new TransformedBitmap(formatConvertedBitmap, new ScaleTransform(scaleX, scaleY));

            int width = smallBitmap.PixelWidth;
            int height = smallBitmap.PixelHeight;
            int stride = width * 4;
            int bufLen = height * stride;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                smallBitmap.CopyPixels(pixels, stride, 0);

                return ExtractSampledColor(pixels, width, height);
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

    // Random(42) uses the same samples on every call. Generate them once;
    // immutable coordinates and stack-local buckets also allow concurrent calls.
    private static readonly double[] SampleCoordinates = CreateSampleCoordinates();

    private static double[] CreateSampleCoordinates()
    {
#pragma warning disable S2245 // Deterministic artwork sampling, not cryptography
        var random = new Random(42);
#pragma warning restore S2245
        var coordinates = new double[600];
        for (int i = 0; i < coordinates.Length; i++) coordinates[i] = random.NextDouble();
        return coordinates;
    }

    private struct ColorBucket
    {
        public int Count;
        public int SumR;
        public int SumG;
        public int SumB;
    }

    internal static Color ExtractSampledColor(ReadOnlySpan<byte> pixels, int width, int height)
    {
        // 0..255 / 50 produces six bins per channel, or 216 buckets total.
        Span<ColorBucket> buckets = stackalloc ColorBucket[216];
        buckets.Clear();
        Span<int> order = stackalloc int[216];
        int used = 0;
        for (int i = 0; i < SampleCoordinates.Length; i += 2)
        {
            int x = (int)(SampleCoordinates[i] * width);
            int y = (int)(SampleCoordinates[i + 1] * height);
            int index = (y * width + x) * 4;
            byte b = pixels[index], g = pixels[index + 1], r = pixels[index + 2];
            int brightness = (r + g + b) / 3;
            if (brightness < 60 || brightness > 245 || pixels[index + 3] < 100) continue;

            int key = (r / 50 * 6 + g / 50) * 6 + b / 50;
            ref ColorBucket bucket = ref buckets[key];
            if (bucket.Count == 0) order[used++] = key;
            bucket.Count++;
            bucket.SumR += r;
            bucket.SumG += g;
            bucket.SumB += b;
        }

        ColorBucket best = default;
        // Preserve the previous dictionary's first-seen tie-breaking order.
        for (int i = 0; i < used; i++)
        {
            ref ColorBucket bucket = ref buckets[order[i]];
            if (bucket.Count > best.Count) best = bucket;
        }
        if (best.Count == 0) return Color.FromRgb(255, 255, 255);
        var color = Color.FromRgb((byte)(best.SumR / best.Count),
            (byte)(best.SumG / best.Count), (byte)(best.SumB / best.Count));
        return EnsureMinimumBrightness(EnhanceSaturation(color, 1.3), 100);
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
