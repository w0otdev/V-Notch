using System.Collections.Concurrent;
using VNotch.Benchmarks;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using VNotch.Controllers;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Type Controller = typeof(LiquidGlassController);

    [STAThread]
    static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "results.json";
        var results = new List<object>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8)) };
        Console.WriteLine("Actual CPU frame processing; excludes desktop capture, WPF upload/composition and display refresh. FPS below is CPU throughput, NOT on-screen FPS.");
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {Environment.ProcessorCount} logical CPUs");
        foreach (var (w, h) in new[] { (515, 280), (960, 304), (1120, 1020) })
        foreach (int sigma in new[] { 0, 8, 20 })
        {
            var glass = new LiquidGlassController(new Image(), () => IntPtr.Zero, () => null);
            byte[] pixels = new byte[w * h * 4];
            new Random(42).NextBytes(pixels);
            IntPtr source = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                Marshal.Copy(pixels, 0, source, pixels.Length);
                Set("_dibBits", source);
                Type dimensions = Controller.GetNestedType("MapDimensions", BindingFlags.NonPublic)!;
                object dims = Activator.CreateInstance(dimensions, w, h, w, h, 0, w, h, 0, 0, 0, 0)!;
                var parameters = LiquidGlassController.GlassParams.Default;
                Controller.GetMethod("EnsureMaps", Private)!.Invoke(glass, new[] { (object)parameters, dims });
                var radii = (int[])Controller.GetMethod("GaussianBoxRadii", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { (double)sigma, 3 })!;
                var blur = Controller.GetMethod("BlurCapturedSource", Private)!.CreateDelegate<Func<int, int, int[], byte[]>>(glass);
                var refract = Controller.GetMethod("Refract", Private)!.CreateDelegate<Action<LiquidGlassController.GlassParams, byte[]?>>(glass);
                object history = Controller.GetField("_frameHistory", Private)!.GetValue(glass)!;
                var unchanged = history.GetType().GetMethod("IsUnchanged", Private)!.CreateDelegate<Func<IntPtr, int, int, bool>>(history);
                var commit = history.GetType().GetMethod("Commit", Private)!.CreateDelegate<Action<IntPtr, int, int>>(history);
                byte[] baselinePixels = new byte[pixels.Length], baselineScratch = new byte[pixels.Length];
                byte[] BaselineBlur()
                {
                    Marshal.Copy(source, baselinePixels, 0, pixels.Length);
                    foreach (int passRadius in radii)
                    {
                        int r = Math.Clamp(passRadius, 0, Math.Max(1, Math.Min(w, h) / 2));
                        if (r < 1) continue;
                        Parallel.ForEach(Partitioner.Create(0, h), parallel, range =>
                            BaselineBoxBlur.BoxBlurHorizontal(baselinePixels, baselineScratch, w, r, range.Item1, range.Item2));
                        Parallel.ForEach(Partitioner.Create(0, w), parallel, range =>
                            BaselineBoxBlur.BoxBlurVertical(baselineScratch, baselinePixels, w, h, r, range.Item1, range.Item2));
                    }
                    return baselinePixels;
                }
                byte[]? blurred = sigma == 0 ? null : blur(w, h, radii);
                byte[]? originalBlurred = sigma == 0 ? null : BaselineBlur();
                if (sigma > 0 && !originalBlurred!.AsSpan().SequenceEqual(blurred)) throw new Exception("Blur pixels differ");
                refract(parameters, originalBlurred);
                byte[] originalOutput = (byte[])((byte[])Controller.GetField("_outBuffer", Private)!.GetValue(glass)!).Clone();
                refract(parameters, blurred);
                byte[] outputPixels = (byte[])Controller.GetField("_outBuffer", Private)!.GetValue(glass)!;
                if (!originalOutput.AsSpan().SequenceEqual(outputPixels)) throw new Exception("Refracted pixels differ");
                string hash = Convert.ToHexString(SHA256.HashData(outputPixels));
                byte value = pixels[0];
                // Same stages as ProcessCpuFrame on a changed frame, identical for
                // both paths: exact comparison, blur, refraction, history copy.
                // Capture/presentation and idle-state bookkeeping are excluded.
                void Frame(bool baseline)
                {
                    Marshal.WriteByte(source, value ^= 255);
                    _ = unchanged(source, w, h);
                    byte[]? input = sigma == 0 ? null : baseline ? BaselineBlur() : blur(w, h, radii);
                    refract(parameters, input);
                    commit(source, w, h);
                }
                var warmup = Stopwatch.StartNew();
                while (warmup.ElapsedMilliseconds < 1000) { Frame(true); Frame(false); }
                double[] beforeTimes = new double[210], afterTimes = new double[210];
                for (int round = 0; round < 7; round++)
                {
                    if ((round & 1) == 0) { Batch(true, beforeTimes, round); Batch(false, afterTimes, round); }
                    else { Batch(false, afterTimes, round); Batch(true, beforeTimes, round); }
                }
                Array.Sort(beforeTimes); Array.Sort(afterTimes);
                double before = beforeTimes[105], after = afterTimes[105];
                var blurTiming = sigma == 0 ? (Median: 0.0, P95: 0.0, Alloc: 0.0) : Measure(() => blur(w, h, radii));
                var refraction = Measure(() => refract(parameters, blurred));
                results.Add(new { Width = w, Height = h, Sigma = sigma, BeforeMs = before, AfterMs = after,
                    BeforeP95Ms = beforeTimes[199], AfterP95Ms = afterTimes[199], BeforeCpuFps = 1000 / before,
                    AfterCpuFps = 1000 / after, ThroughputGainPercent = (before / after - 1) * 100,
                    BlurMs = blurTiming.Median, RefractionMs = refraction.Median, PixelHash = hash });
                Console.WriteLine($"{w}x{h} sigma={sigma}: {before:F3} -> {after:F3}ms; CPU {1000 / before:F1} -> {1000 / after:F1} fps ({(before / after - 1) * 100:F1}%); blur {blurTiming.Median:F3}ms; hash {hash[..12]}");
                void Batch(bool baseline, double[] times, int round)
                {
                    for (int n = 0; n < 30; n++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        Frame(baseline);
                        times[round * 30 + n] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    }
                }
                void Set(string name, object value) => Controller.GetField(name, Private)!.SetValue(glass, value);
            }
            finally { Marshal.FreeHGlobal(source); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    static (double Median, double P95, double Alloc) Measure(Action action)
    {
        const int rounds = 7, frames = 30;
        double[] times = new double[rounds * frames];
        long allocated = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        Array.Sort(times);
        return (times[times.Length / 2], times[(int)(times.Length * .95)], (double)allocated / times.Length);
    }
}
