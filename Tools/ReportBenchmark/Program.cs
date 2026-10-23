using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq.Expressions;
using VNotch.Services;
using VNotch.Models;

internal static class Program
{
    private sealed record Result(string Name, int Iterations, double MedianUs, double MinUs, double MaxUs, double BytesPerOp);
    private static readonly List<Result> Results = new();

    [STAThread]
    private static void Main(string[] args)
    {
        var colors = typeof(FastBlurService).Assembly.GetType("VNotch.Services.DynamicIslandColorExtractor")!;
        var contrast = colors.GetMethod("EnsureTextOnDarkBackground")!.CreateDelegate<Func<Color, Color, double, Color>>();
        int colorIndex = 0;
        Color[] samples = Enumerable.Range(0, 256).Select(i => Color.FromRgb((byte)i, (byte)(i * 37), (byte)(i * 79))).ToArray();
        Measure("text-contrast", 100_000, () => contrast(samples[colorIndex++ & 255], Colors.Black, 7));
        Measure("baseline-text-contrast", 100_000, () => VNotch.BenchmarkBaseline.DynamicIslandColorExtractor.EnsureTextOnDarkBackground(samples[colorIndex++ & 255], Colors.Black, 7));
        bool historicalColorsMatch = samples.All(c => contrast(c, Colors.Black, 7) == VNotch.BenchmarkBaseline.DynamicIslandColorExtractor.EnsureTextOnDarkBackground(c, Colors.Black, 7));
        var toHsl = colors.GetMethod("ToHsl")!.CreateDelegate<Func<Color, (double H, double S, double L)>>();
        var fromHsl = colors.GetMethod("HslToColor")!.CreateDelegate<Func<double, double, double, Color>>();
        var luminance = colors.GetMethod("GetRelativeLuminance")!.CreateDelegate<Func<Color, double>>();
        var ratio = colors.GetMethod("GetContrastRatio")!.CreateDelegate<Func<Color, Color, double>>();
        Color ReferenceContrast(Color color)
        {
            var hsl = toHsl(color);
            var best = color;
            if (luminance(best) < .18 || hsl.L < .40)
                best = fromHsl(hsl.H, Math.Max(.18, hsl.S), Math.Clamp(Math.Max(hsl.L, .60), .55, .65));
            if (ratio(best, Colors.Black) >= 7 && luminance(best) >= .18) return best;
            for (int step = 0; step <= 100; step++)
            {
                double lightness = hsl.L + (1 - hsl.L) * (step / 100.0);
                var candidate = fromHsl(hsl.H, Math.Max(.18, hsl.S), Math.Clamp(lightness, .55, .72));
                if (ratio(candidate, Colors.Black) >= 7 && luminance(candidate) >= .18) return candidate;
            }
            return Colors.White;
        }
        bool sameColors = samples.All(c => contrast(c, Colors.Black, 7) == ReferenceContrast(c));

        var blur = typeof(FastBlurService).GetMethod("BoxBlurVertical", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Action<byte[], byte[], int, int, int>>();
        var baselineBlur = typeof(VNotch.BenchmarkBaseline.FastBlurService).GetMethod("BoxBlurVertical", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Action<byte[], byte[], int, int, int>>();
        bool sameBlur = true;
        foreach (int size in new[] {128, 192})
        {
            var pixels = new byte[size * size * 4];
            new Random(42).NextBytes(pixels);
            var output = new byte[pixels.Length];
            Measure($"vertical-blur-{size}", 1500, () => blur(pixels, output, size, size, 8));
            var baselineOutput = new byte[pixels.Length];
            Measure($"baseline-vertical-blur-{size}", 1500, () => baselineBlur(pixels, baselineOutput, size, size, 8));
            sameBlur &= output.SequenceEqual(baselineOutput);
        }

        var bitmapParameter = Expression.Parameter(typeof(BitmapSource));
        var palette = Expression.Lambda<Action<BitmapSource>>(Expression.Block(
            Expression.Call(colors.GetMethod("GetDynamicIslandPalette", new[] { typeof(BitmapSource) })!, bitmapParameter), Expression.Empty()), bitmapParameter).Compile();
        var dim = colors.GetMethod("GetBrightnessDimOverlay")!.CreateDelegate<Func<BitmapSource, double>>();
        var artworkPixels = new byte[128 * 128 * 4];
        new Random(21).NextBytes(artworkPixels);
        for (int i = 3; i < artworkPixels.Length; i += 4) artworkPixels[i] = 255;
        BitmapSource Artwork()
        {
            var bitmap = BitmapSource.Create(128, 128, 96, 96, PixelFormats.Bgra32, null, artworkPixels, 128 * 4);
            bitmap.Freeze();
            return bitmap;
        }
        Measure("palette-plus-dim-cold", 300, () => { var b = Artwork(); palette(b); dim(b); });
        Measure("baseline-palette-plus-dim-cold", 300, () => { var b = Artwork(); VNotch.BenchmarkBaseline.DynamicIslandColorExtractor.GetDynamicIslandPalette(b); VNotch.BenchmarkBaseline.DynamicIslandColorExtractor.GetBrightnessDimOverlay(b); });

        var diagnostics = PerformanceDiagnosticService.Instance;
        Measure("service-log-full-buffer", 100_000, () => diagnostics.AddServiceLog(PerformanceHealthLevel.Nominal, "BENCH", "fixed message"));
        RuntimeLog.MinimumLevel = LogLevel.Info;
        Measure("disabled-debug-interpolation", 100_000, () => RuntimeLog.Debug("BENCH", $"Position {colorIndex} value {colorIndex * 1.1:F3}"));

        using var scanner = new WindowTitleScanner();
        var lastScan = typeof(WindowTitleScanner).GetField("_lastWindowEnumTime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Measure("desktop-window-scan", 150, () => { lastScan.SetValue(scanner, DateTime.MinValue); scanner.GetAllWindowTitles(false); });

        string json = JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.Now, Runtime = Environment.Version.ToString(), CPUs = Environment.ProcessorCount,
            SameContrastOutput = sameColors, MatchesHistoricalContrastOutput = historicalColorsMatch,
            ContrastOracle = "Linear scan using corrected HSL channel clamping; historical baseline is retained for timing and reported separately",
            SameBlurOutput = sameBlur, Results }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (args.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!); File.WriteAllText(args[0], json); }
        if (!sameColors || !sameBlur) throw new InvalidOperationException("Production output differs from the correctness reference; see the saved report.");
    }

    private static void Measure(string name, int iterations, Action action)
    {
        for (int i = 0; i < Math.Min(iterations, 2000); i++) action();
        var times = new double[7];
        double allocated = 0;
        for (int round = 0; round < times.Length; round++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) action();
            times[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
            allocated += (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)iterations;
        }
        Array.Sort(times);
        Results.Add(new(name, iterations, times[3], times[0], times[^1], allocated / times.Length));
    }
}
