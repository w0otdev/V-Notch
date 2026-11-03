using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Controllers;

internal static class FpsProbe
{
    internal static int Run(bool visible = false)
    {
        var window = new Window { ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent };
        var image = new Image(); window.Content = image;
        window.Width = 320; window.Height = 64; window.Left = 40; window.Top = 40;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var glass = new LiquidGlassController(image, () => hwnd,
            () => new LiquidGlassController.CaptureRegion(40, 40, 320, 64), maxRegionWidth: 400, maxRegionHeight: 100);
        var wake = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
        wake.Tick += (_, _) => glass.ForceRefresh();
        Exception? failure = null;
        int presented = 0;
        var latencies = new List<double>();
        var renderTimes = new List<double>();
        var waitTimes = new List<double>();
        var submissionLatency = typeof(LiquidGlassController).GetProperty("LastSubmissionLatencyMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            if (!glass.SetGpuMode(true, _ => { }, ex => failure = ex, ultimateGpu: true)) throw new InvalidOperationException("GPU unavailable");
            var presenter = typeof(LiquidGlassController).GetField("_d3dPresenter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(glass)!;
            var renderProperty = presenter.GetType().GetProperty("SubmittedGpuRenderMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var waitProperty = presenter.GetType().GetProperty("SubmittedGpuWaitMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
            presenter.GetType().GetEvent("FramePresented")!.AddEventHandler(presenter, (Action<LiquidGlassController.GpuGeometry>)(_ =>
            {
                presented++;
                latencies.Add((double)submissionLatency.GetValue(glass)!);
                renderTimes.Add((double)renderProperty.GetValue(presenter)!);
                waitTimes.Add((double)waitProperty.GetValue(presenter)!);
            }));
            if (visible) window.Show();
            glass.SetAnimating(true); glass.Start(); wake.Start();
            double Measure(int target)
            {
                glass.UpdateFps(target); Pump(1200); presented = 0; latencies.Clear(); renderTimes.Clear(); waitTimes.Clear();
                var watch = Stopwatch.StartNew(); Pump(2000);
                if (failure != null) throw failure;
                double actual = presented / watch.Elapsed.TotalSeconds;
                Console.WriteLine($"Target={target} FPS, actual WPF submissions={actual:F1} FPS with continuous 1ms wake-ups");
                latencies.Sort();
                Console.WriteLine($"Render-to-WPF submission latency median={latencies[latencies.Count / 2]:F2} ms p95={latencies[(int)((latencies.Count - 1) * .95)]:F2} ms");
                Console.WriteLine($"Native render mean={renderTimes.Average():F2} ms, worker fence wait mean={waitTimes.Average():F2} ms, GPU retries={presenter.GetType().GetProperty("GpuCompletionRetries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(presenter)}, image busy retries={presenter.GetType().GetProperty("ImageBusyRetries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(presenter)}");
                if (actual > target + 2 || actual < 5) throw new InvalidOperationException("Target cap was bypassed or frames stopped");
                return actual;
            }
            double low = Measure(30), high = Measure(120);
            Console.WriteLine(presenter.GetType().GetProperty("CaptureStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(presenter));
            Console.WriteLine($"Worker GPU timeouts={presenter.GetType().GetProperty("GpuWorkerTimeouts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(presenter)}");
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (module.ModuleName == "VNotch.Native.dll") Console.WriteLine($"Native module: {module.FileName}");
            if (high <= low * 1.15) throw new InvalidOperationException("Changing the target did not increase the submission rate");
            Console.WriteLine("PASS: live target changes affect frame submission and wake-ups respect the cap.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAIL: {ex}"); return 1; }
        finally { wake.Stop(); glass.Stop(); window.Close(); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
