using System.Diagnostics;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "compare") return Compare(args[1], args[2]);
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: RenderPerformanceBenchmark <application DLL> <output JSON> | compare <before JSON> <after JSON>");
                return 2;
            }
            string assemblyPath = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            string appDirectory = Path.GetDirectoryName(assemblyPath)!;
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string dependency = Path.Combine(appDirectory, name.Name + ".dll");
                return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
            };
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            _ = new Application(); // No windows, devices, timers, or app startup services.
            Type visualizer = assembly.GetType("VNotch.Controls.MusicVisualizer", throwOnError: true)!;
            var pixels = CapturePixels(visualizer);
            var measurements = MeasureVisualizer(visualizer);
            measurements.AddRange(MeasureProgress(assembly));
            var report = new Report(assemblyPath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
                RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, Environment.ProcessorCount,
                visualizer.GetMethod("UpdateDrawingFrame", PrivateInstance) != null,
                pixels, measurements);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
            Console.WriteLine($"Captured {pixels.Count} raw-pixel SHA256 comparisons and {measurements.Count} component measurements to {output}");
            Console.WriteLine("These are CPU drawing updates/offscreen raster measurements, not display FPS, GPU utilization, or whole-app RAM.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static List<PixelCase> CapturePixels(Type type)
    {
        var results = new List<PixelCase>();
        var probe = new VisualizerProbe(type);
        int sequence = 0;
        // One control deliberately traverses resizes, DPI and brush changes so
        // stale retained resources cannot pass by constructing a fresh instance.
        foreach (double dpi in new[] { 96.0, 120.0, 144.0, 192.0 })
        foreach (var size in new[] { new Size(32, 20), new Size(48, 28), new Size(117.5, 56) })
        foreach (Color color in new[] { Colors.White, Color.FromRgb(37, 168, 217), Color.FromArgb(157, 224, 71, 42) })
        {
            probe.Layout(size, dpi);
            probe.SetBrush(color);
            foreach (double mix in new[] { 0.0, 0.025, 0.09, 0.179, 0.18, 0.5, 1.0, 0.0 })
            foreach (double check in new[] { 0.0, 0.37, 1.0 })
            {
                probe.SetFrame(sequence++, "morph", mix, check);
                probe.Frame();
                var bitmap = probe.Raster();
                int stride = bitmap.PixelWidth * 4;
                byte[] raw = new byte[stride * bitmap.PixelHeight];
                bitmap.CopyPixels(raw, stride, 0);
                if (!raw.Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0))
                    throw new InvalidOperationException("Empty visualizer raster cannot establish visual equivalence.");
                results.Add(new PixelCase($"{dpi}/{size.Width:R}x{size.Height:R}/{color}/{mix:R}/{check:R}",
                    bitmap.PixelWidth, bitmap.PixelHeight, Convert.ToHexString(SHA256.HashData(raw))));
            }
        }
        return results;
    }

    private static List<Measurement> MeasureVisualizer(Type type)
    {
        var results = new List<Measurement>();
        foreach (double dpi in new[] { 96.0, 144.0 })
        foreach (string scenario in new[] { "bars", "snapped", "morph", "icon" })
        foreach (bool raster in new[] { false, true })
        {
            var probe = new VisualizerProbe(type);
            probe.Layout(new Size(48, 28), dpi);
            probe.SetBrush(Color.FromRgb(37, 168, 217));
            probe.SetFrame(0, scenario);
            probe.Frame();
            var bitmap = probe.Raster();
            void Frame(int frame)
            {
                probe.SetFrame(frame, scenario);
                probe.Frame(allowDrawingSuppression: true);
                if (raster) { bitmap.Clear(); bitmap.Render(probe.Visual); }
            }
            results.Add(Measure($"visualizer/{scenario}/{dpi}/{(raster ? "raster" : "update")}",
                raster ? 200 : 5000, Frame));
        }
        return results;
    }

    private static IEnumerable<Measurement> MeasureProgress(Assembly assembly)
    {
        Type type = assembly.GetType("VNotch.ViewModels.ProgressViewModel", true)!;
        object model = type.GetConstructors().Single().Invoke(new object?[] { null });
        object info = Activator.CreateInstance(assembly.GetType("VNotch.Models.MediaInfo", true)!)!;
        type.GetProperty("CurrentMediaInfo")!.SetValue(model, info);
        type.GetField("_lastKnownDuration", PrivateInstance)!.SetValue(model, TimeSpan.FromMinutes(5.25));
        var setPosition = Setter<TimeSpan>(type, "_lastKnownPosition", model);
        var render = type.GetMethod("Render")!.CreateDelegate<Action>(model);
        foreach (int cadence in new[] { 60, 144 })
        {
            yield return Measure($"progress/{cadence}/update", 30000, frame =>
            {
                setPosition(TimeSpan.FromTicks((frame % (cadence * 120)) * TimeSpan.TicksPerSecond / cadence));
                render();
            });
        }
    }

    private static Measurement Measure(string name, int frames, Action<int> frame)
    {
        var warmup = Stopwatch.StartNew();
        while (warmup.ElapsedMilliseconds < 500)
            for (int i = 0; i < frames; i++) frame(i);
        const int rounds = 9;
        double[] times = new double[rounds], allocations = new double[rounds];
        for (int round = 0; round < rounds; round++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < frames; i++) frame(i);
            times[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / frames;
            allocations[round] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)frames;
        }
        double[] sortedTimes = times.Order().ToArray();
        double[] sortedAllocations = allocations.Order().ToArray();
        var result = new Measurement(name, frames, sortedTimes[rounds / 2], sortedAllocations[rounds / 2], times, allocations);
        Console.WriteLine($"{name}: {result.MedianUs:F3} us/frame; {result.MedianAllocatedBytes:F2} B/frame");
        return result;
    }

    private static Action<T> Setter<T>(Type type, string field, object owner)
    {
        var value = Expression.Parameter(typeof(T), "value");
        var assignment = Expression.Assign(Expression.Field(Expression.Constant(owner, type),
            type.GetField(field, PrivateInstance)!), value);
        return Expression.Lambda<Action<T>>(assignment, value).Compile();
    }

    private static int Compare(string beforePath, string afterPath)
    {
        var before = JsonSerializer.Deserialize<Report>(File.ReadAllText(beforePath))!;
        var after = JsonSerializer.Deserialize<Report>(File.ReadAllText(afterPath))!;
        var mismatches = before.Pixels.Where((item, index) => index >= after.Pixels.Count
            || item.Name != after.Pixels[index].Name || item.Width != after.Pixels[index].Width
            || item.Height != after.Pixels[index].Height || item.Sha256 != after.Pixels[index].Sha256).ToArray();
        if (before.Pixels.Count != after.Pixels.Count || mismatches.Length != 0)
        {
            Console.Error.WriteLine($"Raw raster mismatch: {mismatches.Length} / {before.Pixels.Count} cases; before={before.Pixels.Count}, after={after.Pixels.Count}");
            foreach (var mismatch in mismatches.Take(8)) Console.Error.WriteLine(mismatch.Name);
            return 1;
        }
        if (before.Measurements.Count != after.Measurements.Count
            || !before.Measurements.Select(item => item.Name).Order().SequenceEqual(after.Measurements.Select(item => item.Name).Order()))
        {
            Console.Error.WriteLine("Measurement cases differ; rerun both assemblies with the same runner.");
            return 1;
        }
        Console.WriteLine($"All {before.Pixels.Count} raw-pixel hashes match exactly.");
        Console.WriteLine("Case | before us/frame | after us/frame | time reduction | before B/frame | after B/frame");
        foreach (var original in before.Measurements)
        {
            var current = after.Measurements.Single(item => item.Name == original.Name);
            Console.WriteLine($"{original.Name} | {original.MedianUs:F3} | {current.MedianUs:F3} | {(1 - current.MedianUs / original.MedianUs) * 100:F1}% | {original.MedianAllocatedBytes:F2} | {current.MedianAllocatedBytes:F2}");
        }
        return 0;
    }

    private sealed class VisualizerProbe
    {
        private readonly FrameworkElement _control;
        private readonly Type _type;
        private readonly Action<DrawingContext> _render;
        private readonly Func<bool>? _update;
        private readonly Func<bool>? _needsDrawing;
        private readonly Action<double> _opacity, _icon, _check, _play;
        private readonly double[] _heights;
        private bool _recorded;
        public DrawingVisual Visual { get; } = new();
        private Size _size;
        private double _dpi;

        public VisualizerProbe(Type type)
        {
            _type = type;
            _control = (FrameworkElement)Activator.CreateInstance(type)!;
            _render = (type.GetMethod("RenderFrame", PrivateInstance)
                ?? type.GetMethod("OnRender", PrivateInstance)!).CreateDelegate<Action<DrawingContext>>(_control);
            _update = type.GetMethod("UpdateDrawingFrame", PrivateInstance)?.CreateDelegate<Func<bool>>(_control);
            _needsDrawing = type.GetMethod("NeedsDrawingUpdate", PrivateInstance)?.CreateDelegate<Func<bool>>(_control);
            _opacity = Setter<double>(type, "_currentOpacity", _control);
            _icon = Setter<double>(type, "_iconMix", _control);
            _check = Setter<double>(type, "_checkMix", _control);
            _play = Setter<double>(type, "_playMix", _control);
            _heights = (double[])type.GetField("_drawHeights", PrivateInstance)!.GetValue(_control)!;
        }

        public void Layout(Size size, double dpi)
        {
            _size = size; _dpi = dpi;
            _control.Measure(size);
            _control.Arrange(new Rect(size));
            _type.GetField("_cachedDpi", PrivateInstance)!.SetValue(_control, new DpiScale(dpi / 96, dpi / 96));
        }

        public void SetBrush(Color color) => _type.GetProperty("ActiveBrush")!.SetValue(_control, new SolidColorBrush(color));

        public void SetFrame(int frame, string scenario, double? exactIcon = null, double? exactCheck = null)
        {
            double phase = frame * 0.017;
            for (int i = 0; i < _heights.Length; i++)
                _heights[i] = scenario == "snapped" ? 0.31 + 0.002 * (frame % 2)
                    : 0.08 + 0.92 * (0.5 + 0.5 * Math.Sin(phase + i * 0.7));
            _opacity(scenario == "snapped" ? 0.75 : 0.4 + 0.6 * (0.5 + 0.5 * Math.Cos(phase)));
            _icon(exactIcon ?? (scenario is "bars" or "snapped" ? 0 : scenario == "icon" ? 1 : 0.5 + 0.5 * Math.Sin(phase)));
            _check(exactCheck ?? (0.5 + 0.5 * Math.Cos(phase * 0.7)));
            _play(0.5 + 0.5 * Math.Sin(phase * 0.4));
        }

        public void Frame(bool allowDrawingSuppression = false)
        {
            if (_recorded && allowDrawingSuppression && _needsDrawing != null && !_needsDrawing()) return;
            if (_recorded && _update != null) _update();
            else
            {
                using var drawing = Visual.RenderOpen();
                _render(drawing);
                _recorded = true;
            }
        }

        public RenderTargetBitmap Raster()
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_size.Width * _dpi / 96),
                (int)Math.Ceiling(_size.Height * _dpi / 96), _dpi, _dpi, PixelFormats.Pbgra32);
            bitmap.Render(Visual);
            return bitmap;
        }
    }

    private sealed record PixelCase(string Name, int Width, int Height, string Sha256);
    private sealed record Measurement(string Name, int FramesPerRound, double MedianUs, double MedianAllocatedBytes, double[] RoundUs, double[] RoundAllocatedBytes);
    private sealed record Report(string AssemblyPath, string AssemblySha256, string Framework, string OperatingSystem,
        int LogicalProcessors, bool RetainedVisualizer, List<PixelCase> Pixels, List<Measurement> Measurements);
}
