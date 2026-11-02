using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controllers;

// Benchmark only: real capture/refraction/WPF bridge, synthetic movement, no desktop readback.
internal static class BenchmarkProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double MiB = 1048576.0;
    internal static string ResultDirectory { get; set; } = "artifacts";
    internal static string ResultPath(string name)
    {
        Directory.CreateDirectory(ResultDirectory);
        return Path.GetFullPath(Path.Combine(ResultDirectory, name));
    }
    private sealed record RunResult(string Scenario, int Width, int Height, int TargetFps, double BlurPercent,
        int Repeat, double Seconds, int Frames, double SubmittedFps, double LatencyMedianMs, double LatencyP95Ms,
        double LatencyP99Ms, double IntervalP95Ms, double IntervalP99Ms, double LateIntervalsPercent,
        double CpuMachinePercent, double CpuCorePercent, double ManagedAllocMiBPerSecond,
        double PrivateStartMiB, double PrivateEndMiB, double PrivatePeakMiB, double WorkingSetPeakMiB,
        int Gen0, int Gen1, int Gen2, long GpuRetries, long ImageRetries, long WorkerTimeouts,
        ulong ConsumerRetries, double IntervalMaxMs, bool? CadencePassed, string[] GateFailures);

    internal static int Run(bool focused = false, int durationMs = 3000, int repeats = 3)
    {
        BenchmarkChecks.SelfCheck();
        string output = ResultPath("glass-benchmark-results.json");
        var results = new List<RunResult>();
        string? failure = null, status = null;
        object? startup = null;
        double dpi = 1;
        using var process = Process.GetCurrentProcess();
        var window = new Window { Width = 320, Height = 64, Left = 128, Top = 96,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent };
        var canvas = new Canvas { ClipToBounds = true };
        var image = new Image { Stretch = Stretch.Fill };
        canvas.Children.Add(image); window.Content = canvas;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int width = 320, height = 64, phase = 0, count = 0;
        bool moving = true, recording = false;
        int sampleCapacity = durationMs * 144 / 1000 + 1024;
        double[] latencies = new double[sampleCapacity], intervals = new double[sampleCapacity];
        long previousFrame = 0;
        Exception? renderFailure = null;
        using var glass = new LiquidGlassController(image, () => hwnd,
            () => new LiquidGlassController.CaptureRegion(160, 120, width, height),
            maxRegionWidth: 1200, maxRegionHeight: 600) { CropGpuPresentationToCapture = true, HideFromScreenCapture = true };
        var wake = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        wake.Tick += (_, _) => glass.ForceRefresh();
        var resize = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        void SetSize(int w, int h)
        {
            width = w; height = h;
            window.Width = w / dpi; window.Height = h / dpi;
            glass.SetLiveRegion(new(160 + phase, 120, width, height));
        }
        resize.Tick += (_, _) => SetSize(width == 320 ? 1200 : 320, height == 64 ? 600 : 64);
        try
        {
            SetSize(320, 64); window.Left = 160 / dpi; window.Top = 120 / dpi;
            window.Show();
            process.Refresh(); long beforePrivate = process.PrivateMemorySize64;
            long beforeAlloc = GC.GetTotalAllocatedBytes(true);
            var startupWatch = Stopwatch.StartNew();
            if (!glass.SetGpuMode(true, g =>
            {
                image.Width = g.SrcW / dpi; image.Height = g.SrcH / dpi;
                Canvas.SetLeft(image, -g.OffX / dpi); Canvas.SetTop(image, -g.OffY / dpi);
            }, ex => renderFailure = ex, ultimateGpu: true)) throw new InvalidOperationException("GPU unavailable");
            typeof(LiquidGlassController).GetMethod("EnableNativeRefraction", Private)!.Invoke(glass, null);
            var presenter = typeof(LiquidGlassController).GetField("_d3dPresenter", Private)!.GetValue(glass)!;
            Func<T> Getter<T>(object target, string name) => (Func<T>)target.GetType().GetProperty(name, Private)!.GetMethod!.CreateDelegate(typeof(Func<T>), target);
            var latency = Getter<double>(glass, "LastSubmissionLatencyMs");
            var gpuRetries = Getter<long>(presenter, "GpuCompletionRetries");
            var imageRetries = Getter<long>(presenter, "ImageBusyRetries");
            var timeouts = Getter<long>(presenter, "GpuWorkerTimeouts");
            var consumerRetries = Getter<ulong>(presenter, "GpuConsumerRetries");
            presenter.GetType().GetEvent("FramePresented")!.AddEventHandler(presenter, (Action<LiquidGlassController.GpuGeometry>)(_ =>
            {
                long now = Stopwatch.GetTimestamp();
                if (recording)
                {
                    int index = count++;
                    if (index < latencies.Length)
                    {
                        latencies[index] = latency();
                        intervals[index] = previousFrame == 0 ? 0 : (now - previousFrame) * 1000.0 / Stopwatch.Frequency;
                    }
                    previousFrame = now;
                }
                if (moving)
                {
                    phase = (phase + 1) % 8;
                    glass.SetLiveRegion(new(160 + phase, 120, width, height));
                    glass.ForceRefresh();
                }
            }));
            glass.SetBlur(28 * .05 * .05 * dpi);
            glass.SetAnimating(true); glass.Start(); wake.Start();
            for (int i = 0; i < 100 && !glass.HasPresentedFrame && renderFailure == null; i++) Pump(100);
            if (renderFailure != null) throw renderFailure;
            if (!glass.HasPresentedFrame) throw new TimeoutException("No first frame");
            process.Refresh();
            startup = new { FirstFrameMs = startupWatch.Elapsed.TotalMilliseconds,
                PrivateDeltaMiB = (process.PrivateMemorySize64 - beforePrivate) / MiB,
                ManagedAllocMiB = (GC.GetTotalAllocatedBytes(true) - beforeAlloc) / MiB };

            RunResult Measure(string name, int target, double blur, int repeat, int sampleMs)
            {
                count = 0; previousFrame = 0;
                process.Refresh();
                long privateStart = process.PrivateMemorySize64, privatePeak = privateStart, workingPeak = process.WorkingSet64;
                TimeSpan cpu = process.TotalProcessorTime;
                long allocated = GC.GetTotalAllocatedBytes(true);
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                long retryStart = gpuRetries(), busyStart = imageRetries(), timeoutStart = timeouts();
                ulong consumerStart = consumerRetries();
                var memory = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                memory.Tick += (_, _) => { process.Refresh(); privatePeak = Math.Max(privatePeak, process.PrivateMemorySize64); workingPeak = Math.Max(workingPeak, process.WorkingSet64); };
                string phaseName = $"{name}/{width}x{height}/{target}/{blur}";
                BenchmarkEvents.Log.PhaseStart(phaseName, repeat);
                var watch = Stopwatch.StartNew();
                recording = true; memory.Start();
                try { Pump(sampleMs); }
                finally { recording = false; memory.Stop(); }
                double seconds = watch.Elapsed.TotalSeconds;
                BenchmarkEvents.Log.PhaseStop(phaseName, repeat);
                ulong consumerDelta = consumerRetries() - consumerStart;
                long allocatedDelta = GC.GetTotalAllocatedBytes(true) - allocated;
                process.Refresh();
                double cpuCore = (process.TotalProcessorTime - cpu).TotalSeconds / seconds * 100;
                if (renderFailure != null) throw renderFailure;
                if (moving && count < 5) throw new InvalidOperationException("Frame submission stopped");
                if (count > latencies.Length) throw new InvalidOperationException("Frame sampling capacity exceeded; FPS cap may be bypassed");
                double[] sortedLatency = latencies[..count]; Array.Sort(sortedLatency);
                double[] sortedInterval = intervals.Take(count).Where(v => v > 0).Order().ToArray();
                double Q(double[] values, double q) => values.Length == 0 ? 0 : values[(int)Math.Ceiling((values.Length - 1) * q)];
                double intervalP95 = Q(sortedInterval, .95);
                double latePercent = sortedInterval.Length == 0 ? 0 : sortedInterval.Count(v => v > 1500.0 / target) * 100.0 / sortedInterval.Length;
                string[] gateFailures = name == "idle" ? [] : BenchmarkChecks.Cadence(target, count / seconds, intervalP95, latePercent);
                var result = new RunResult(name, width, height, target, blur, repeat, seconds, count, count / seconds,
                    Q(sortedLatency, .5), Q(sortedLatency, .95), Q(sortedLatency, .99), intervalP95, Q(sortedInterval, .99), latePercent,
                    cpuCore / Environment.ProcessorCount, cpuCore, allocatedDelta / MiB / seconds,
                    privateStart / MiB, process.PrivateMemorySize64 / MiB, Math.Max(privatePeak, process.PrivateMemorySize64) / MiB,
                    Math.Max(workingPeak, process.WorkingSet64) / MiB,
                    GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2,
                    gpuRetries() - retryStart, imageRetries() - busyStart, timeouts() - timeoutStart,
                    consumerDelta, Q(sortedInterval, 1), name == "idle" ? null : gateFailures.Length == 0, gateFailures);
                results.Add(result);
                Console.WriteLine($"{name} {width}x{height} blur={blur}% target={target} run={repeat}: {result.SubmittedFps:F1} FPS, latency p95={result.LatencyP95Ms:F2} ms, interval p95={result.IntervalP95Ms:F2} ms, CPU={result.CpuMachinePercent:F2}% machine, alloc={result.ManagedAllocMiBPerSecond:F3} MiB/s, private delta={result.PrivateEndMiB - result.PrivateStartMiB:F2} MiB, consumer retries={consumerDelta}, cadence={(result.CadencePassed is null ? "observed" : result.CadencePassed.Value ? "PASS" : "FAIL")}");
                return result;
            }

            foreach (var size in new[] { (320, 64), (800, 420), (1200, 600) })
            {
                SetSize(size.Item1, size.Item2);
                foreach (int target in focused ? new[] { 120 } : new[] { 30, 60, 120, 144 })
                {
                    glass.UpdateFps(target); glass.SetBlur(28 * .05 * .05 * dpi); Pump(1200);
                    for (int repeat = 1; repeat <= repeats; repeat++) Measure("moving", target, 5, repeat, durationMs);
                }
                glass.UpdateFps(120); glass.SetBlur(28 * dpi); Pump(1200);
                for (int repeat = 1; repeat <= repeats; repeat++) Measure("moving", 120, 100, repeat, durationMs);
            }
            glass.UpdateFps(120); glass.SetBlur(28 * .05 * .05 * dpi);
            resize.Start(); Pump(1200);
            for (int repeat = 1; repeat <= repeats; repeat++) Measure("resize-250ms", 120, 5, repeat, Math.Max(durationMs, 4000));
            resize.Stop(); moving = false; wake.Stop();
            SetSize(320, 64); glass.SetAnimating(false); glass.SetIdle(true); Pump(1800);
            for (int repeat = 1; repeat <= repeats; repeat++) Measure("idle", 120, 5, repeat, durationMs);
            status = (string)presenter.GetType().GetProperty("CaptureStatus", Private)!.GetValue(presenter)!;
            Console.WriteLine(status);
            return 0;
        }
        catch (Exception ex) { failure = ex.ToString(); Console.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            wake.Stop(); resize.Stop(); glass.Stop(); window.Close();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string native = Path.Combine(AppContext.BaseDirectory, "VNotch.Native.dll");
            File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow,
                Framework = RuntimeInformation.FrameworkDescription, Os = RuntimeInformation.OSDescription,
                LogicalProcessors = Environment.ProcessorCount, DpiScale = dpi, NativeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native))),
                CadenceGate = "Every active repeat: FPS 95%-102% of target, p95 interval <=1.5 frame budgets, <=1% intervals above1.5 budgets. Idle is observational because desktop changes are uncontrolled.",
                CadencePassed = results.Any(r => r.CadencePassed.HasValue) && results.Where(r => r.CadencePassed.HasValue).All(r => r.CadencePassed == true),
                Workload = "Visible WPF, native refraction, moving crop forces source/blur rebuild each frame; 1ms wake stress. CPU/memory include benchmark harness. FPS means submissions, not DWM presents.",
                Startup = startup, CaptureStatus = status, Failure = failure, Runs = results }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Results: " + output);
        }
    }

    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
