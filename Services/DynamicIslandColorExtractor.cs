using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class DynamicIslandColorExtractor
{
    public readonly record struct Palette(Color Main, Color Sub);
    public readonly record struct PaletteColor(Color Color, int Population, double Score);

    private const int PreprocessSize = 150;
    private const int KClusters = 6;
    private const int KMeansMaxIter = 12;

    private static WeakReference<BitmapSource>? _lastPaletteBitmap;
    private static Palette _lastPaletteResult;

    #region Public entry points

    public static Palette GetDynamicIslandPalette(BitmapSource bitmap)
    {
        if (_lastPaletteBitmap != null && _lastPaletteBitmap.TryGetTarget(out var cached) && ReferenceEquals(cached, bitmap))
            return _lastPaletteResult;

        var result = ExtractAdvancedPalette(bitmap, Rect.Empty);
        Palette palette;
        if (result.IsMonotone || result.Primary == default)
        {
            RuntimeLog.Log("COLOR-PICK",
                $"FALLBACK: IsMonotone={result.IsMonotone} Primary={result.Primary} (R={result.Primary.R},G={result.Primary.G},B={result.Primary.B})");
            palette = new Palette(Color.FromRgb(34, 34, 34), Color.FromRgb(180, 180, 180));
        }
        else
        {
            RuntimeLog.Log("COLOR-PICK",
                $"OK: Primary=({result.Primary.R},{result.Primary.G},{result.Primary.B}) Secondary=({result.Secondary.R},{result.Secondary.G},{result.Secondary.B})");
            var main = result.Primary;
            var darkUiBackground = Colors.Black;
            var sub = EnsureTextOnDarkBackground(main, darkUiBackground, 4.5);
            palette = new Palette(main, sub);
        }

        _lastPaletteBitmap = new WeakReference<BitmapSource>(bitmap);
        _lastPaletteResult = palette;
        return palette;
    }

    public static Palette GetDynamicIslandPalette(BitmapSource bitmap, Rect smartCropBbox)
    {
        var result = ExtractAdvancedPalette(bitmap, smartCropBbox);
        if (result.IsMonotone || result.Primary == default)
        {
            RuntimeLog.Log("COLOR-PICK",
                $"FALLBACK(bbox): IsMonotone={result.IsMonotone} Primary={result.Primary} (R={result.Primary.R},G={result.Primary.G},B={result.Primary.B})");
            return new Palette(Color.FromRgb(34, 34, 34), Color.FromRgb(180, 180, 180));
        }

        RuntimeLog.Log("COLOR-PICK",
            $"OK(bbox): Primary=({result.Primary.R},{result.Primary.G},{result.Primary.B}) Secondary=({result.Secondary.R},{result.Secondary.G},{result.Secondary.B})");
        var main = result.Primary;
        var darkUiBackground = Colors.Black;
        var sub = EnsureTextOnDarkBackground(main, darkUiBackground, 4.5);
        return new Palette(main, sub);
    }

    public static Color GetDominantColor(BitmapSource bitmap)
    {
        var result = ExtractAdvancedPalette(bitmap, Rect.Empty);
        if (result.IsMonotone) return Colors.White;
        return result.Primary != default ? result.Primary : Color.FromRgb(30, 30, 30);
    }

    public static Color EnsureBrightColor(Color c)
    {
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (luminance < 0.5)
        {
            const double factor = 0.7;
            byte r = (byte)(c.R + (255 - c.R) * factor);
            byte g = (byte)(c.G + (255 - c.G) * factor);
            byte b = (byte)(c.B + (255 - c.B) * factor);
            return Color.FromRgb(r, g, b);
        }
        return c;
    }

    public static Color GetVibrantColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;
        double d = max - min;
        if (d > 0)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2) / 6.0;
            else h = ((r - g) / d + 4) / 6.0;
            s = Math.Min(s, 0.90);
        }

        if (s < 0.16)
        {
            byte gray = (byte)Math.Clamp(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B, 0, 255);
            byte lifted = (byte)Math.Clamp(gray + (255 - gray) * 0.18, 0, 255);
            return Color.FromRgb(lifted, lifted, lifted);
        }

        l = Math.Max(l, 0.65);
        l = Math.Min(l, 0.85);
        return HslToColor(h, s, l);
    }

    public static double GetAdaptiveBlurOpacity(double luminance, double brightnessBoost = 1.0)
    {
        brightnessBoost = Math.Clamp(brightnessBoost, 0.5, 2.5);
        double baseOpacity;
        if (luminance <= 0.72)
            baseOpacity = 0.55 + (brightnessBoost - 0.5) * (0.45 / 2.0);
        else
        {
            double t = Math.Clamp((luminance - 0.72) / 0.28, 0.0, 1.0);
            double fullBase = 0.55 + (brightnessBoost - 0.5) * (0.45 / 2.0);
            baseOpacity = fullBase - t * 0.10;
        }
        return Math.Clamp(baseOpacity, 0.0, 1.0);
    }

    public static double GetAdaptiveBlurImageOpacity(double luminance)
    {
        if (luminance <= 0.70) return 0.90;
        double t = Math.Clamp((luminance - 0.70) / 0.30, 0.0, 1.0);
        return 0.90 - t * 0.15;
    }

    public static double GetBrightnessDimOverlay(BitmapSource bitmap)
    {
        if (bitmap == null) return 0;

        try
        {
            int sampleSize = 32;
            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var scaled = new TransformedBitmap(formatted,
                new ScaleTransform((double)sampleSize / formatted.PixelWidth, (double)sampleSize / formatted.PixelHeight));

            int stride = sampleSize * 4;
            int bufLen = sampleSize * sampleSize * 4;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                scaled.CopyPixels(pixels, stride, 0);

                double totalLuminance = 0;
                int brightPixelCount = 0;
                int pixelCount = sampleSize * sampleSize;

                for (int i = 0; i < bufLen; i += 4)
                {
                    double r = pixels[i + 2] / 255.0;
                    double g = pixels[i + 1] / 255.0;
                    double b = pixels[i] / 255.0;

                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    totalLuminance += lum;

                    if (lum > 0.75) brightPixelCount++;
                }

                double avgLuminance = totalLuminance / pixelCount;
                double brightRatio = (double)brightPixelCount / pixelCount;

                double overlayOpacity = 0;

                if (avgLuminance > 0.55)
                {
                    double t = Math.Clamp((avgLuminance - 0.55) / 0.45, 0.0, 1.0);
                    overlayOpacity = t * 0.45;
                }

                if (brightRatio > 0.40)
                {
                    double t = Math.Clamp((brightRatio - 0.40) / 0.50, 0.0, 1.0);
                    overlayOpacity = Math.Max(overlayOpacity, t * 0.40);
                }

                double combined = overlayOpacity;
                if (avgLuminance > 0.55 && brightRatio > 0.40)
                {
                    double lumContrib = Math.Clamp((avgLuminance - 0.55) / 0.45, 0.0, 1.0) * 0.45;
                    double ratioContrib = Math.Clamp((brightRatio - 0.40) / 0.50, 0.0, 1.0) * 0.40;
                    combined = Math.Max(lumContrib, ratioContrib) + Math.Min(lumContrib, ratioContrib) * 0.3;
                }

                return Math.Clamp(combined, 0.0, 0.65);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region Advanced palette extraction (K-Means HSV + weighted zones)

    public readonly record struct PaletteResult(
        Color Primary, Color Secondary, Color Accent,
        bool IsMonotone, bool IsFlatBg, Color TextOnPrimary);

    private static PaletteResult ExtractAdvancedPalette(BitmapSource bitmap, Rect smartCropBbox, int analysisSize = 36)
    {
        if (bitmap == null)
            return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);

        try
        {
            var formattedBitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            double scaleX = (double)analysisSize / formattedBitmap.PixelWidth;
            double scaleY = (double)analysisSize / formattedBitmap.PixelHeight;
            var small = new TransformedBitmap(formattedBitmap, new ScaleTransform(scaleX, scaleY));

            int width = small.PixelWidth;
            int height = small.PixelHeight;
            if (width <= 4 || height <= 4)
                return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);

            int stride = width * 4;
            int bufLen = height * stride;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                small.CopyPixels(pixels, stride, 0);

                const int NUM_BUCKETS = 36;
                float[] bucketSatSum = new float[NUM_BUCKETS];
                float[] bucketValSum = new float[NUM_BUCKETS];
                float[] bucketWeight = new float[NUM_BUCKETS];
                int[] bucketCount = new int[NUM_BUCKETS];
                float[] bucketPeakS = new float[NUM_BUCKETS];
                float[] bucketPeakH = new float[NUM_BUCKETS];
                float[] bucketPeakV = new float[NUM_BUCKETS];

                float centerX = width / 2f, centerY = height / 2f;
                int totalColorPixels = 0;
                int totalPixels = 0;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * stride + x * 4;
                        byte a = pixels[i + 3];
                        if (a < 80) continue;

                        float rf = pixels[i + 2] / 255f;
                        float gf = pixels[i + 1] / 255f;
                        float bf = pixels[i] / 255f;

                        var (h, s, v) = RgbToHsv(rf, gf, bf);
                        totalPixels++;

                        if (v < 0.06f) continue;
                        if (s < 0.12f) continue;

                        totalColorPixels++;

                        int bucket = (int)(h * NUM_BUCKETS) % NUM_BUCKETS;
                        if (bucket < 0) bucket += NUM_BUCKETS;

                        float dx = (x - centerX) / centerX;
                        float dy = (y - centerY) / centerY;
                        float dist = MathF.Sqrt(dx * dx + dy * dy);
                        float zoneWeight = 1.0f + MathF.Max(0, 1.0f - dist) * 0.5f;

                        bucketSatSum[bucket] += s * zoneWeight;
                        bucketValSum[bucket] += v * zoneWeight;
                        bucketWeight[bucket] += zoneWeight;
                        bucketCount[bucket]++;

                        float vibrancy = s * MathF.Max(v, 0.3f);
                        if (vibrancy > bucketPeakS[bucket])
                        {
                            bucketPeakS[bucket] = vibrancy;
                            bucketPeakH[bucket] = h;
                            bucketPeakV[bucket] = Math.Max(v, 0.4f);
                        }
                    }
                }

                bool isMonotone = totalPixels > 0 && (float)totalColorPixels / totalPixels < 0.08f;
                if (isMonotone)
                    return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);

            float bestScore = -1, secondScore = -1;
            int bestBucket = -1, secondBucket = -1;

            for (int i = 0; i < NUM_BUCKETS; i++)
            {
                if (bucketCount[i] == 0) continue;

                float area = (float)bucketCount[i] / Math.Max(totalColorPixels, 1);
                float avgSat = bucketSatSum[i] / bucketWeight[i];
                float avgVal = bucketValSum[i] / bucketWeight[i];

                float score = MathF.Pow(area, 0.3f) * avgSat * avgVal;

                if (score > bestScore)
                {
                    if (bestBucket >= 0)
                    {
                        int hueDist = Math.Min(Math.Abs(i - bestBucket), NUM_BUCKETS - Math.Abs(i - bestBucket));
                        if (hueDist >= 4 && bestScore > secondScore)
                        {
                            secondScore = bestScore;
                            secondBucket = bestBucket;
                        }
                    }
                    bestScore = score;
                    bestBucket = i;
                }
                else if (score > secondScore)
                {
                    int hueDist = Math.Min(Math.Abs(i - bestBucket), NUM_BUCKETS - Math.Abs(i - bestBucket));
                    if (hueDist >= 4)
                    {
                        secondScore = score;
                        secondBucket = i;
                    }
                }
            }

            Color primary = Color.FromRgb(30, 30, 30);
            if (bestBucket >= 0)
            {
                float pH = bucketPeakH[bestBucket];
                float pS = Math.Min(bucketPeakS[bestBucket] / Math.Max(bucketPeakV[bestBucket], 0.3f), 1.0f);
                float pV = bucketPeakV[bestBucket];
                pV = Math.Max(pV, 0.45f);
                pS = Math.Max(pS, 0.50f);
                primary = HsvToColor(pH, pS, pV);
            }

            Color secondary = default;
            if (secondBucket >= 0)
            {
                float pH = bucketPeakH[secondBucket];
                float pS = Math.Min(bucketPeakS[secondBucket] / Math.Max(bucketPeakV[secondBucket], 0.3f), 1.0f);
                float pV = bucketPeakV[secondBucket];
                pV = Math.Max(pV, 0.45f);
                pS = Math.Max(pS, 0.50f);
                secondary = HsvToColor(pH, pS, pV);
            }

            bool isFlatBg = bestScore > 0 && secondScore > 0 &&
                            (bestScore / (bestScore + secondScore)) > 0.85f;

            double primaryLum = GetRelativeLuminance(primary);
            Color textOnPrimary = primaryLum > 0.4 ? Colors.Black : Colors.White;

            return new PaletteResult(primary, secondary, secondary, false, isFlatBg, textOnPrimary);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
        }
    }
    catch
    {
        return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);
    }
}

    private static bool IsValidCluster(float h, float s, float v)
    {
        if (s < 0.12f && v > 0.92f) return false;
        if (v < 0.10f) return false;
        float hDeg = h * 360f;
        if (hDeg >= 10f && hDeg <= 25f && s < 0.40f) return false;
        return true;
    }

    private static float DeltaEHsv(float h1, float s1, float v1, float h2, float s2, float v2)
    {
        float dh = Math.Min(Math.Abs(h1 - h2), 1f - Math.Abs(h1 - h2)) * 2f;
        float ds = Math.Abs(s1 - s2);
        float dv = Math.Abs(v1 - v2);
        return MathF.Sqrt(dh * dh * 10000f + ds * ds * 2500f + dv * dv * 2500f);
    }

    #endregion

    #region K-Means HSV clustering

    private sealed class HsvCluster
    {
        public float H, S, V;
        public float Coverage;
        public bool InCropZone;
    }

    private static List<HsvCluster> KMeansHsv(List<(float H, float S, float V, float Weight)> samples, int k, int maxIter)
    {
        var rng = new Random(42);
        var centroids = new (float H, float S, float V)[k];
        int firstIdx = rng.Next(samples.Count);
        centroids[0] = (samples[firstIdx].H, samples[firstIdx].S, samples[firstIdx].V);

        for (int i = 1; i < k; i++)
        {
            float maxDist = -1;
            int bestIdx = 0;
            for (int j = 0; j < Math.Min(samples.Count, 200); j++)
            {
                int idx = rng.Next(samples.Count);
                float minD = float.MaxValue;
                for (int ci = 0; ci < i; ci++)
                {
                    float d = HsvDist(samples[idx].H, samples[idx].S, samples[idx].V,
                                      centroids[ci].H, centroids[ci].S, centroids[ci].V);
                    if (d < minD) minD = d;
                }
                if (minD > maxDist) { maxDist = minD; bestIdx = idx; }
            }
            centroids[i] = (samples[bestIdx].H, samples[bestIdx].S, samples[bestIdx].V);
        }

        int[] assignments = new int[samples.Count];
        for (int iter = 0; iter < maxIter; iter++)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                float minDist = float.MaxValue;
                int best = 0;
                for (int c = 0; c < k; c++)
                {
                    float d = HsvDist(samples[i].H, samples[i].S, samples[i].V,
                                      centroids[c].H, centroids[c].S, centroids[c].V);
                    if (d < minDist) { minDist = d; best = c; }
                }
                assignments[i] = best;
            }

            var sinH = new float[k]; var cosH = new float[k];
            var sumS = new float[k]; var sumV = new float[k]; var sumW = new float[k];
            for (int i = 0; i < samples.Count; i++)
            {
                int c = assignments[i];
                float w = samples[i].Weight;
                float angle = samples[i].H * MathF.PI * 2f;
                sinH[c] += MathF.Sin(angle) * w;
                cosH[c] += MathF.Cos(angle) * w;
                sumS[c] += samples[i].S * w;
                sumV[c] += samples[i].V * w;
                sumW[c] += w;
            }

            for (int c = 0; c < k; c++)
            {
                if (sumW[c] > 0)
                {
                    float avgH = MathF.Atan2(sinH[c] / sumW[c], cosH[c] / sumW[c]) / (MathF.PI * 2f);
                    if (avgH < 0) avgH += 1f;
                    centroids[c] = (avgH, sumS[c] / sumW[c], sumV[c] / sumW[c]);
                }
            }
        }

        float totalWeight = samples.Sum(s => s.Weight);
        var clusterWeights = new float[k];
        var inCrop = new bool[k];

        for (int i = 0; i < samples.Count; i++)
        {
            clusterWeights[assignments[i]] += samples[i].Weight;
            if (samples[i].Weight >= 2.0f) inCrop[assignments[i]] = true;
        }

        var result = new List<HsvCluster>(k);
        for (int c = 0; c < k; c++)
        {
            if (clusterWeights[c] <= 0) continue;
            result.Add(new HsvCluster
            {
                H = centroids[c].H,
                S = centroids[c].S,
                V = centroids[c].V,
                Coverage = clusterWeights[c],
                InCropZone = inCrop[c]
            });
        }

        return result;
    }

    private static float HsvDist(float h1, float s1, float v1, float h2, float s2, float v2)
    {
        float dh = Math.Min(Math.Abs(h1 - h2), 1f - Math.Abs(h1 - h2)) * 2f;
        float ds = s1 - s2;
        float dv = v1 - v2;
        return dh * dh + ds * ds + dv * dv;
    }

    #endregion

    #region HSV / HSL / contrast helpers

    private static (float H, float S, float V) RgbToHsv(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float d = max - min;
        float h = 0, s = max > 0 ? d / max : 0, v = max;

        if (d > 0.001f)
        {
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6f;
        }

        return (h, s, v);
    }

    private static Color HsvToColor(float h, float s, float v)
    {
        float r, g, b;
        int hi = (int)(h * 6f) % 6;
        float f = h * 6f - hi;
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);

        switch (hi)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        return Color.FromRgb(
            (byte)Math.Clamp(r * 255f, 0, 255),
            (byte)Math.Clamp(g * 255f, 0, 255),
            (byte)Math.Clamp(b * 255f, 0, 255));
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;
        double d = max - min;
        if (d > 0.0001)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2) / 6.0;
            else h = ((r - g) / d + 4) / 6.0;
        }
        return (h, s, l);
    }

    public static Color HslToColor(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return Color.FromRgb(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    public static double GetRelativeLuminance(Color c)
    {
        static double Linear(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    public static double GetContrastRatio(Color a, Color b)
    {
        double l1 = GetRelativeLuminance(a);
        double l2 = GetRelativeLuminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    public static double ColorDistance(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    public static Color EnsureTextOnDarkBackground(Color color, Color background, double minRatio)
    {
        var hsl = ToHsl(color);
        Color best = color;

        if (GetRelativeLuminance(best) < 0.18 || hsl.L < 0.40)
            best = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(Math.Max(hsl.L, 0.60), 0.55, 0.65));

        if (GetContrastRatio(best, background) >= minRatio && GetRelativeLuminance(best) >= 0.18)
            return best;

        for (int step = 0; step <= 100; step++)
        {
            double t = step / 100.0;
            double l = hsl.L + (1.0 - hsl.L) * t;
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.55, 0.72));
            if (GetContrastRatio(candidate, background) >= minRatio && GetRelativeLuminance(candidate) >= 0.18)
                return candidate;
        }

        return Colors.White;
    }

    public static Color EnsureContrast(Color sub, Color main, double minRatio)
    {
        var hsl = ToHsl(sub);
        bool lighten = GetRelativeLuminance(main) < 0.45;
        Color best = sub;
        double bestRatio = GetContrastRatio(best, main);

        for (int step = 0; step <= 100 && bestRatio < minRatio; step++)
        {
            double t = step / 100.0;
            double l = lighten ? hsl.L + (1.0 - hsl.L) * t : hsl.L * (1.0 - t);
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.0, 1.0));
            double ratio = GetContrastRatio(candidate, main);
            if (ratio > bestRatio) { best = candidate; bestRatio = ratio; }
        }

        if (bestRatio < minRatio)
        {
            var whiteRatio = GetContrastRatio(Colors.White, main);
            var blackRatio = GetContrastRatio(Colors.Black, main);
            best = whiteRatio >= blackRatio ? Colors.White : Colors.Black;
        }

        return best;
    }

    #endregion
}
