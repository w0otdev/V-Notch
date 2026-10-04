using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ContrastSearchTests
{
    [Fact]
    public void SaturatedChannelsStayAt255WhenLightnessIncreases()
    {
        for (int step = 50; step <= 100; step++)
        {
            var color = DynamicIslandColorExtractor.HslToColor(5.0 / 6, 1, step / 100.0);
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
            DynamicIslandColorExtractor.EnsureContrast(color, background, ratio));
    }

    private static Color ScanText(Color color, Color background, double minRatio)
    {
        var hsl = DynamicIslandColorExtractor.ToHsl(color);
        Color best = color;
        if (DynamicIslandColorExtractor.GetRelativeLuminance(best) < 0.18 || hsl.L < 0.40)
            best = DynamicIslandColorExtractor.HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(Math.Max(hsl.L, 0.60), 0.55, 0.65));
        if (DynamicIslandColorExtractor.GetContrastRatio(best, background) >= minRatio &&
            DynamicIslandColorExtractor.GetRelativeLuminance(best) >= 0.18) return best;
        for (int step = 0; step <= 100; step++)
        {
            double l = hsl.L + (1.0 - hsl.L) * (step / 100.0);
            var candidate = DynamicIslandColorExtractor.HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0.55, 0.72));
            if (DynamicIslandColorExtractor.GetContrastRatio(candidate, background) >= minRatio &&
                DynamicIslandColorExtractor.GetRelativeLuminance(candidate) >= 0.18) return candidate;
        }
        return Colors.White;
    }

    private static Color ScanContrast(Color sub, Color main, double minRatio)
    {
        var hsl = DynamicIslandColorExtractor.ToHsl(sub);
        bool lighten = DynamicIslandColorExtractor.GetRelativeLuminance(main) < 0.45;
        Color best = sub;
        double bestRatio = DynamicIslandColorExtractor.GetContrastRatio(best, main);
        for (int step = 0; step <= 100 && bestRatio < minRatio; step++)
        {
            double t = step / 100.0;
            double l = lighten ? hsl.L + (1.0 - hsl.L) * t : hsl.L * (1.0 - t);
            var candidate = DynamicIslandColorExtractor.HslToColor(hsl.H, Math.Max(0.18, hsl.S), Math.Clamp(l, 0, 1));
            double ratio = DynamicIslandColorExtractor.GetContrastRatio(candidate, main);
            if (ratio > bestRatio) { best = candidate; bestRatio = ratio; }
        }
        if (bestRatio < minRatio)
            best = DynamicIslandColorExtractor.GetContrastRatio(Colors.White, main) >=
                DynamicIslandColorExtractor.GetContrastRatio(Colors.Black, main) ? Colors.White : Colors.Black;
        return best;
    }
}
