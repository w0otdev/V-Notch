using System.Runtime.InteropServices;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed partial class ContrastSearchTests
{
    [Fact]
    public void SaturatedChannelsStayAt255WhenLightnessIncreases()
    {
        for (int step = 50; step <= 100; step++)
        {
            var color = HslToColor(5.0 / 6, 1, step / 100.0);
            Assert.Equal(255, color.R);
            Assert.Equal(255, color.B);
        }
    }

    [Fact]
    public void SearchesPreserveTheOriginalDiscreteColorsAcrossBothSidesOfTheBackground()
    {
        var random = new Random(1042026);
        double[] ratios = [1, 1.01, 2, 3, 4.5, 7, 21, 22];
        Color[] boundaries = [Colors.Black, Colors.White, Colors.Gray, Colors.Red,
            Colors.Blue, Colors.Lime, Colors.Yellow, Colors.Cyan, Colors.Magenta];
        foreach (var color in boundaries)
            foreach (var background in boundaries)
                foreach (double ratio in ratios)
                    Verify(color, background, ratio);

        for (int i = 0; i < 20000; i++)
        {
            Color color = Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            Color background = Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            Verify(color, background, ratios[i % ratios.Length]);
        }
    }

    private static void Verify(Color color, Color background, double ratio)
    {
        Assert.Equal(ScanText(color, background, ratio),
            DynamicIslandColorExtractor.EnsureTextOnDarkBackground(color, background, ratio));
        Assert.Equal(ScanContrast(color, background, ratio),
            EnsureContrast(color, background, ratio));
    }

    private static Color ScanText(Color color, Color background, double minRatio)
    {
        var hsl = ToHsl(color);
        Color best = color;
        if (GetRelativeLuminance(best) < 0.18 || hsl.L < 0.40)
            best = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(Math.Max(hsl.L, 0.60), 0.55, 0.65));
        if (GetContrastRatio(best, background) >= minRatio &&
            GetRelativeLuminance(best) >= 0.18) return best;
        for (int step = 0; step <= 100; step++)
        {
            double l = hsl.L + (1.0 - hsl.L) * (step / 100.0);
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.55, 0.72));
            if (GetContrastRatio(candidate, background) >= minRatio &&
                GetRelativeLuminance(candidate) >= 0.18) return candidate;
        }
        return Colors.White;
    }

    private static Color ScanContrast(Color sub, Color main, double minRatio)
    {
        var hsl = ToHsl(sub);
        bool lighten = GetRelativeLuminance(main) < 0.45;
        Color best = sub;
        double bestRatio = GetContrastRatio(best, main);
        for (int step = 0; step <= 100 && bestRatio < minRatio; step++)
        {
            double t = step / 100.0;
            double l = lighten ? hsl.L + (1.0 - hsl.L) * t : hsl.L * (1.0 - t);
            var candidate = HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0, 1));
            double ratio = GetContrastRatio(candidate, main);
            if (ratio > bestRatio) { best = candidate; bestRatio = ratio; }
        }
        if (bestRatio < minRatio)
            best = GetContrastRatio(Colors.White, main) >=
                GetContrastRatio(Colors.Black, main) ? Colors.White : Colors.Black;
        return best;
    }

    internal static (double H, double S, double L) ToHsl(Color c)
    {
        vn_dicolor_to_hsl(c.R, c.G, c.B, out double h, out double s, out double l);
        return (h, s, l);
    }

    internal static Color HslToColor(double h, double s, double l)
    {
        vn_dicolor_hsl_to_color(h, s, l, out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    internal static double GetRelativeLuminance(Color c)
    {
        vn_dicolor_get_relative_luminance(c.R, c.G, c.B, out double lum);
        return lum;
    }

    internal static double GetContrastRatio(Color a, Color b)
    {
        vn_dicolor_get_contrast_ratio(a.R, a.G, a.B, b.R, b.G, b.B, out double ratio);
        return ratio;
    }

    internal static Color EnsureContrast(Color sub, Color main, double minRatio)
    {
        vn_dicolor_ensure_contrast(
            sub.R, sub.G, sub.B,
            main.R, main.G, main.B,
            minRatio,
            out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_to_hsl(
        byte r, byte g, byte b, out double out_h, out double out_s, out double out_l);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_hsl_to_color(
        double h, double s, double l, out byte out_r, out byte out_g, out byte out_b);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_relative_luminance(
        byte r, byte g, byte b, out double out_lum);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_contrast_ratio(
        byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, out double out_ratio);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_ensure_contrast(
        byte sr, byte sg, byte sb, byte mr, byte mg, byte mb, double min_ratio,
        out byte out_r, out byte out_g, out byte out_b);
}
