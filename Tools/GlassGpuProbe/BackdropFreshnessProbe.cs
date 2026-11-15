using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VNotch.Controllers;

internal static class BackdropFreshnessProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("VNotch.Native.dll")]
    private static extern int vn_backdrop_freshness_debug(IntPtr hwnd, [Out] int[] values);

    internal static int RunSource()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var view = new WebView2();
        var window = new Window
        {
            Left = 0, Top = SystemParameters.WorkArea.Top, Width = SystemParameters.PrimaryScreenWidth,
            Height = 650, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Content = view
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                string profile = BenchmarkProbe.ResultPath("freshness-webview-profile");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await view.EnsureCoreWebView2Async(environment);
                Console.WriteLine(JsonSerializer.Serialize(new { hwnd = new WindowInteropHelper(window).Handle.ToInt64() }));
                view.CoreWebView2.WebMessageReceived += (_, e) => Console.WriteLine(e.WebMessageAsJson);
                view.NavigateToString("""
                    <!doctype html><html><head><style>
                    html,body { margin:0;width:100%;height:100%;overflow:hidden;background:rgb(32,96,160) }
                    </style></head><body><script>
                    let frame=0, started=performance.now();
                    function tick() {
                      frame++;
                      const red=performance.now()-started<2000 ? 32 : 32+(frame*47)%192;
                      document.body.style.backgroundColor=`rgb(${red},96,160)`;
                      chrome.webview.postMessage({frame});
                      requestAnimationFrame(tick);
                    }
                    requestAnimationFrame(tick);
                    </script></body></html>
                    """);
            }
            catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message })); app.Shutdown(1); }
        };
        window.Show();
        return app.Run();
    }

    internal static int Run(int targetFps = 144, int top = 0)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int sourceFrame = 0;
        long sourceHwnd = 0;
        string? sourceError = null;
        using var source = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "--freshness-source", "--output-dir", Path.GetFullPath(BenchmarkProbe.ResultDirectory) }
        }) ?? throw new InvalidOperationException("Cannot start controlled Chromium source");
        var sourceReader = Task.Run(async () =>
        {
            while (await source.StandardOutput.ReadLineAsync() is { } line)
            {
                using var message = JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("frame", out var frame)) Volatile.Write(ref sourceFrame, frame.GetInt32());
                if (message.RootElement.TryGetProperty("hwnd", out var handle)) Volatile.Write(ref sourceHwnd, handle.GetInt64());
                if (message.RootElement.TryGetProperty("error", out var error)) sourceError = error.GetString();
            }
        });
        var image = new Image { Stretch = Stretch.Fill };
        var canvas = new Canvas { ClipToBounds = true };
        canvas.Children.Add(image);
        var window = new Window
        {
            Left = (SystemParameters.PrimaryScreenWidth - 320) / 2, Top = top, Width = 320, Height = 64,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Topmost = true, Background = Brushes.Transparent, Content = canvas
        };
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int x = (int)(window.Left * dpi), y = (int)(window.Top * dpi);
        int width = (int)(window.Width * dpi), height = (int)(window.Height * dpi);
        using var glass = new LiquidGlassController(image, () => hwnd, () => new(x, y, width, height),
            activeFps: targetFps, maxRegionWidth: 1600, maxRegionHeight: 600) { CropGpuPresentationToCapture = true };
        bool measuring = false;
        int submissions = 0, changed = 0, validCaptures = 0, uploadedPixel = 0, previousPixel = 0;
        long lastChanged = 0;
        var gaps = new List<double>();
        var invalidPixels = new Dictionary<int, int>();
        Exception? failure = null;
        try
        {
            var startup = Stopwatch.StartNew();
            while (Volatile.Read(ref sourceFrame) < 20 && startup.ElapsedMilliseconds < 10000 && sourceError == null)
                BenchmarkProbe.Pump(20);
            if (sourceError != null) throw new InvalidOperationException(sourceError);
            if (Volatile.Read(ref sourceFrame) < 20) throw new TimeoutException("No controlled Chromium frames");
            // Let the native source-window entrance fade finish before using
            // visible screen pixels to validate its captured backing surface.
            BenchmarkProbe.Pump(700);
            var captureDebug = new int[28];
            vn_backdrop_freshness_debug((IntPtr)Volatile.Read(ref sourceHwnd), captureDebug);
            window.Show();
            if (!glass.SetGpuMode(true, g =>
            {
                image.Width = g.SrcW / dpi; image.Height = g.SrcH / dpi;
                Canvas.SetLeft(image, -g.OffX / dpi); Canvas.SetTop(image, -g.OffY / dpi);
            }, ex => failure = ex)) throw new InvalidOperationException("GPU unavailable");
            var type = typeof(LiquidGlassController);
            type.GetMethod("EnableNativeRefraction", Private)!.Invoke(glass, null);
            var bitsField = type.GetField("_dibBits", Private)!;
            var widthField = type.GetField("_dibW", Private)!;
            var heightField = type.GetField("_dibH", Private)!;
            Action onUploaded = () =>
            {
                IntPtr bits = (IntPtr)bitsField.GetValue(glass)!;
                int w = (int)widthField.GetValue(glass)!, h = (int)heightField.GetValue(glass)!;
                if (bits != IntPtr.Zero && w > 0 && h > 0)
                    Volatile.Write(ref uploadedPixel, Marshal.ReadInt32(bits, (w * (h / 2) + w / 2) * 4));
            };
            type.GetProperty("OnGpuFrameUploaded", Private)!.SetValue(glass, onUploaded);
            object presenter = type.GetField("_d3dPresenter", Private)!.GetValue(glass)!;
            presenter.GetType().GetEvent("FramePresented")!.AddEventHandler(presenter, (Action<LiquidGlassController.GpuGeometry>)(_ =>
            {
                if (!measuring) return;
                submissions++;
                int pixel = Volatile.Read(ref uploadedPixel);
                if (Math.Abs(((pixel >> 8) & 255) - 96) <= 3 && Math.Abs((pixel & 255) - 160) <= 3) validCaptures++;
                else invalidPixels[pixel] = invalidPixels.GetValueOrDefault(pixel) + 1;
                if (pixel == previousPixel) return;
                previousPixel = pixel;
                changed++;
                long now = Stopwatch.GetTimestamp();
                if (lastChanged != 0) gaps.Add((now - lastChanged) * 1000.0 / Stopwatch.Frequency);
                lastChanged = now;
            }));
            glass.SetBlur(28 * .1 * .1 * dpi);
            glass.SetIdle(true);
            glass.SetAnimating(false);
            glass.Start();
            BenchmarkProbe.Pump(1500);
            int sourceStart = Volatile.Read(ref sourceFrame);
            var watch = Stopwatch.StartNew();
            measuring = true;
            BenchmarkProbe.Pump(3000);
            measuring = false;
            double seconds = watch.Elapsed.TotalSeconds;
            int sourceFrames = Volatile.Read(ref sourceFrame) - sourceStart;
            if (failure != null) throw failure;
            gaps.Sort();
            double p95 = gaps.Count == 0 ? 0 : gaps[(int)((gaps.Count - 1) * .95)];
            var sourceHandle = (IntPtr)Volatile.Read(ref sourceHwnd);
            bool layered = GetLayeredWindowAttributes(sourceHandle, out _, out byte alpha, out uint flags);
            vn_backdrop_freshness_debug(sourceHandle, captureDebug);
            var result = new
            {
                TargetFps = targetFps, HostTopDip = top,
                SourceFps = sourceFrames / seconds, SubmittedFps = submissions / seconds,
                ChangedBackdropFps = changed / seconds, ChangedGapP95Ms = p95,
                ChangedGapMaxMs = gaps.Count == 0 ? 0 : gaps[^1],
                RetainedPercent = sourceFrames == 0 ? 0 : changed * 100.0 / sourceFrames,
                LastPixel = $"{previousPixel:X8}",
                SourceExtendedStyle = $"{GetWindowLongPtr(sourceHandle, -20).ToInt64():X8}",
                LayeredAttributes = new { Available = layered, Alpha = alpha, Flags = flags },
                CaptureDebug = captureDebug,
                ValidCapturePercent = submissions == 0 ? 0 : validCaptures * 100.0 / submissions,
                InvalidPixels = invalidPixels.OrderByDescending(p => p.Value).Take(5).Select(p => new { Pixel = $"{p.Key:X8}", Count = p.Value }).ToArray(),
                Passed = sourceFrames >= 90 && validCaptures >= submissions * .95 &&
                    changed >= Math.Min(sourceFrames, targetFps * seconds) * .85 && p95 < Math.Max(25, 1500.0 / targetFps)
            };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            File.WriteAllText(BenchmarkProbe.ResultPath("glass-freshness-results.json"), json);
            Console.WriteLine(result.Passed ? "PASS: stationary glass follows fresh Chromium backdrop frames." : "FAIL: glass repeats stale Chromium backdrop frames.");
            return result.Passed ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            glass.Stop(); window.Close();
            if (!source.HasExited) source.Kill(entireProcessTree: true);
            sourceReader.GetAwaiter().GetResult();
        }
    }
}
