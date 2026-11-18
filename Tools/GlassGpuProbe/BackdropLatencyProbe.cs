using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VNotch.Controllers;

internal static class BackdropLatencyProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    private static Window MakeWindow(int x, int y, int width, int height) => new()
    {
        Left = x, Top = y, Width = width, Height = height, Topmost = true, ShowActivated = false,
        ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
    };

    // Exact window capture excludes the lens process. Paint the source in a second process.
    internal static int RunSource()
    {
        var app = new Application();
        var window = MakeWindow(40, 80, 520, 320);
        window.Background = Brushes.Blue;
        window.Show();
        Console.WriteLine("ready");
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } color)
                await window.Dispatcher.InvokeAsync(() => window.Background = color == "red" ? Brushes.Red : Brushes.Blue);
            await window.Dispatcher.InvokeAsync(window.Close);
        });
        return app.Run();
    }

    internal static int Run()
    {
        using var source = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--latency-source")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true
        }) ?? throw new InvalidOperationException("Cannot start backdrop fixture");
        var window = MakeWindow(180, 200, 140, 50);
        window.AllowsTransparency = true;
        // Opaque black under the image prevents a transparent/missing frame from passing the check.
        window.Background = Brushes.Black;
        var image = new Image { Stretch = Stretch.Fill };
        var canvas = new Canvas { ClipToBounds = true };
        canvas.Children.Add(image);
        window.Content = canvas;
        try
        {
            if (source.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() != "ready")
                throw new InvalidOperationException("Backdrop fixture failed to start");
            window.Show();
            var origin = window.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(window);
            var region = new LiquidGlassController.CaptureRegion((int)origin.X, (int)origin.Y,
                (int)(window.Width * dpi.DpiScaleX), (int)(window.Height * dpi.DpiScaleY));
            var hwnd = new WindowInteropHelper(window).Handle;
            using var glass = new LiquidGlassController(image, () => hwnd, () => region,
                activeFps: 144, maxRegionWidth: 320, maxRegionHeight: 120) { CropGpuPresentationToCapture = true };
            Exception? failure = null;
            if (!glass.SetGpuMode(true, g =>
            {
                image.Width = g.SrcW / dpi.DpiScaleX;
                image.Height = g.SrcH / dpi.DpiScaleY;
            }, ex => failure = ex)) throw new InvalidOperationException("GPU unavailable");
            typeof(LiquidGlassController).GetMethod("EnableNativeRefraction", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(glass, null);
            glass.SetIdle(true);
            glass.Start();
            var inside = window.PointToScreen(new Point(70, 25));
            var outside = window.PointToScreen(new Point(-20, 25));
            var dc = GetDC(IntPtr.Zero);
            try
            {
                int ReadColor(Point p)
                {
                    uint pixel = GetPixel(dc, (int)p.X, (int)p.Y);
                    int redMinusBlue = (int)(pixel & 255) - (int)((pixel >> 16) & 255);
                    return Math.Abs(redMinusBlue) > 80 ? Math.Sign(redMinusBlue) : 0;
                }
                var warmup = Stopwatch.StartNew();
                while ((!glass.HasPresentedFrame || ReadColor(inside) != -1 || ReadColor(outside) != -1) &&
                    warmup.ElapsedMilliseconds < 5000) BenchmarkProbe.Pump(10);
                if (!glass.HasPresentedFrame || ReadColor(inside) != -1 || ReadColor(outside) != -1)
                    throw new InvalidOperationException($"Fixture warmup: frame={glass.HasPresentedFrame}, inside={GetPixel(dc, (int)inside.X, (int)inside.Y):X8}, outside={GetPixel(dc, (int)outside.X, (int)outside.Y):X8}", failure);
                double MeasureTransition(int target)
                {
                    source.StandardInput.WriteLine(target == 1 ? "red" : "blue");
                    var clock = Stopwatch.StartNew();
                    double? sourceAt = null, lensAt = null;
                    while ((sourceAt == null || lensAt == null) && clock.ElapsedMilliseconds < 1000)
                    {
                        if (ReadColor(outside) == target) sourceAt ??= clock.Elapsed.TotalMilliseconds;
                        if (ReadColor(inside) == target) lensAt ??= clock.Elapsed.TotalMilliseconds;
                        if (sourceAt == null || lensAt == null) BenchmarkProbe.Pump(1);
                    }
                    if (sourceAt == null || lensAt == null)
                        throw new TimeoutException($"Missed color {target}: source={sourceAt}, lens={lensAt}, outside={GetPixel(dc, (int)outside.X, (int)outside.Y):X8}, inside={GetPixel(dc, (int)inside.X, (int)inside.Y):X8}", failure);
                    return lensAt.Value - sourceAt.Value;
                }
                foreach (bool settled in new[] { true, false })
                {
                    var samples = new double[16];
                    for (int i = 0; i < samples.Length; ++i)
                    {
                        // Vary the phase relative to both the capture clock and the display.
                        BenchmarkProbe.Pump((settled ? 410 : 35) + i * 37 % (settled ? 100 : 30));
                        samples[i] = MeasureTransition(i % 2 == 0 ? 1 : -1);
                    }
                    Array.Sort(samples);
                    double median = (samples[7] + samples[8]) / 2;
                    Console.WriteLine($"Desktop to visible lens, {samples.Length} {(settled ? "settled" : "active")} transitions: median {median:F1} ms, maximum {samples[^1]:F1} ms.");
                    if (median >= 100) return 1;
                }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return 0;
        }
        finally
        {
            window.Close();
            source.StandardInput.Close();
            if (!source.WaitForExit(3000)) source.Kill(true);
        }
    }
}
