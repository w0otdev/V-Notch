using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Controllers;

// Default mode uses a hidden HWND; --visible and benchmark modes show a test window.
// Live desktop capture stays on the GPU without a CPU readback.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = args.ToList();
        TrayBenchmark.ObserveUi = !options.Remove("--no-ui-observer");
        TrayBenchmark.MoveBackdrop = options.Remove("--move-backdrop");
        TrayBenchmark.ProfileUi = options.Remove("--profile-ui");
        TrayBenchmark.WheelScroll = options.Remove("--wheel-scroll");
        TrayBenchmark.ControlledBackdrop = options.Remove("--controlled-backdrop");
        TrayBenchmark.ResizeTray = options.Remove("--resize-tray");
        TrayBenchmark.OpaqueHost = options.Remove("--opaque-host");
        TrayBenchmark.DwmHost = options.Remove("--dwm-host");
        TrayBenchmark.ForegroundHost = options.Remove("--foreground-host");
        int outputIndex = options.IndexOf("--output-dir");
        if (outputIndex >= 0)
        {
            if (outputIndex + 1 == options.Count) throw new ArgumentException("--output-dir needs a path");
            BenchmarkProbe.ResultDirectory = System.IO.Path.GetFullPath(options[outputIndex + 1]);
            options.RemoveRange(outputIndex, 2);
        }
        args = options.ToArray();
        if (args.Contains("--host-alpha-probe")) return DwmProbeHost.Run();
        if (args.Contains("--benchmark-self-check")) { BenchmarkChecks.SelfCheck(); return 0; }
        if (args.Contains("--benchmark") || args.Contains("--benchmark-focused") || args.Contains("--benchmark-resize"))
        {
            bool focused = args.Contains("--benchmark-focused") || args.Contains("--benchmark-resize");
            int ReadOption(string name, int fallback, int minimum, int maximum)
            {
                int index = Array.IndexOf(args, name);
                if (index < 0) return fallback;
                if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value < minimum || value > maximum)
                    throw new ArgumentException($"{name} must be between {minimum} and {maximum}");
                return value;
            }
            return BenchmarkProbe.Run(focused, ReadOption("--duration-ms", focused ? 10000 : 3000, 1000, 60000),
                ReadOption("--repeats", 3, 1, 10), args.Contains("--benchmark-resize"));
        }
        if (args.Length == 2 && args[0] == "--tray-fixture") return TrayBenchmark.Prepare(args[1]);
        if (args.Length == 2 && args[0] == "--tray-fixture-default") return TrayBenchmark.Prepare(args[1], 500);
        if (args.Length == 2 && args[0] == "--tray-benchmark") return TrayBenchmark.Run(args[1]);
        if (args.Length == 2 && args[0] == "--tray-default-benchmark") return TrayBenchmark.Run(args[1], 500);
        if (args.Length == 2 && args[0] == "--tray-solid-benchmark") return TrayBenchmark.Run(args[1], 500, false);
        if (args.Contains("--fps")) return FpsProbe.Run(args.Contains("--visible"));
        if (args.Contains("--main")) return MainWindowProbe.Run();
        var window = new Window
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent
        };
        var image = new Image();
        window.Content = image;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var glass = new LiquidGlassController(image, () => hwnd,
            () => new LiquidGlassController.CaptureRegion(40, 40, 320, 64), maxRegionWidth: 400, maxRegionHeight: 100);
        Exception? failure = null;
        try
        {
            if (!glass.SetGpuMode(true, _ => { }, ex => failure = ex)) throw new InvalidOperationException("GPU initialization failed.");
            // Production starts capture before lazy GPU resource creation.
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                glass.Start();
                var frame = new DispatcherFrame();
                long deadline = Environment.TickCount64 + 5000;
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    if (glass.HasPresentedFrame || failure != null || Environment.TickCount64 >= deadline)
                    { timer.Stop(); frame.Continue = false; }
                };
                timer.Start(); Dispatcher.PushFrame(frame);
                if (failure != null) throw failure;
                if (!glass.HasPresentedFrame || glass.ImageSource is not D3DImage)
                    throw new TimeoutException($"No shared D3DImage frame in session {cycle}.");
                glass.Stop(retainGpuResources: true);
                if (glass.HasPresentedFrame) throw new InvalidOperationException("Stop retained visible frame state.");
            }
            Console.WriteLine("PASS: hidden WPF D3DImage receives GPU frames across three retained-resource sessions.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAIL: {ex}"); return 1; }
        finally { glass.Stop(); window.Close(); }
    }
}
