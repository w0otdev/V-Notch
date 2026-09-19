using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Benchmarks;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        _ = new Application();
        var results = new List<object>();
        Console.WriteLine("Default-theme clock: WPF drawing updates and offscreen rasterization; NOT whole-app/display FPS.");
        foreach (double dpi in new[] { 96.0, 120.0, 144.0, 192.0 })
        {
            var oldClock = new BaselineAnalogClock();
            var newClock = new AnalogClock();
            var current = typeof(AnalogClock).GetMethod("RenderFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<DrawingContext, DateTime>>(newClock);
            var update = typeof(AnalogClock).GetMethod("UpdateFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<DateTime, bool>>(newClock);
            var baseline = (Action<DrawingContext, DateTime>)oldClock.RenderFrame;
            var oldVisual = new DrawingVisual();
            var newVisual = new DrawingVisual();
            DateTime time = new(2026, 9, 19, 10, 8, 23, 456);
            foreach (bool date in new[] { true, false })
            foreach (double size in new[] { 92.0, 117.5, 184.0, 92.0 })
            {
                oldClock.ShowDate = newClock.ShowDate = date;
                Layout(oldClock, size); Layout(newClock, size);
                for (int frame = 0; frame < 8; frame++)
                {
                    time = time.AddHours(3).AddMilliseconds(127);
                    Draw(oldVisual, baseline, time);
                    if (frame == 0 || update(time)) Draw(newVisual, current, time);
                    byte[] before = Pixels(oldVisual, size, dpi), after = Pixels(newVisual, size, dpi);
                    if (!before.AsSpan().SequenceEqual(after))
                        throw new Exception($"Pixel mismatch: dpi={dpi} size={size} date={date} frame={frame}");
                }
            }
            oldClock.ShowDate = newClock.ShowDate = true;
            Layout(oldClock, 92); Layout(newClock, 92);
            Draw(oldVisual, baseline, time); Draw(newVisual, current, time);
            var oldBitmap = new RenderTargetBitmap((int)Math.Ceiling(92 * dpi / 96), (int)Math.Ceiling(92 * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            var newBitmap = new RenderTargetBitmap(oldBitmap.PixelWidth, oldBitmap.PixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            void Frame(bool old, bool raster)
            {
                time = time.AddMilliseconds(33);
                var visual = old ? oldVisual : newVisual;
                if (old) Draw(visual, baseline, time);
                else if (update(time)) Draw(visual, current, time);
                if (raster)
                {
                    var bitmap = old ? oldBitmap : newBitmap;
                    bitmap.Clear();
                    bitmap.Render(visual);
                }
            }
            foreach (bool raster in new[] { false, true })
            {
                var warmup = Stopwatch.StartNew();
                while (warmup.ElapsedMilliseconds < 500) { Frame(true, raster); Frame(false, raster); }
                int frames = raster ? 100 : 2000;
                var oldTimes = new double[7]; var newTimes = new double[7];
                long oldBytes = 0, newBytes = 0;
                for (int round = 0; round < 7; round++)
                {
                    if ((round & 1) == 0) { Run(true); Run(false); } else { Run(false); Run(true); }
                    void Run(bool old)
                    {
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long start = Stopwatch.GetTimestamp();
                        for (int i = 0; i < frames; i++) Frame(old, raster);
                        double us = Stopwatch.GetElapsedTime(start).TotalMicroseconds / frames;
                        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        if (old) { oldTimes[round] = us; oldBytes += allocated; }
                        else { newTimes[round] = us; newBytes += allocated; }
                    }
                }
                Array.Sort(oldTimes); Array.Sort(newTimes);
                double gain = (oldTimes[3] / newTimes[3] - 1) * 100;
                results.Add(new { Dpi = dpi, RasterIncluded = raster, BeforeUs = oldTimes[3], AfterUs = newTimes[3], GainPercent = gain,
                    BeforeAllocatedBytes = oldBytes / (7.0 * frames), AfterAllocatedBytes = newBytes / (7.0 * frames) });
                Console.WriteLine($"dpi={dpi} raster={raster}: {oldTimes[3]:F2} -> {newTimes[3]:F2} us ({gain:+0.0;-0.0}%); allocation {oldBytes / (7.0 * frames):F0} -> {newBytes / (7.0 * frames):F0} B/frame");
            }
        }
        string output = args.Length > 0 ? args[0] : "artifacts/default-theme.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("256 before/after pixel comparisons passed (times, resizes, date toggles, 4 raster DPIs).");
    }
    static void Layout(FrameworkElement element, double size)
    {
        element.Measure(new Size(size, size));
        element.Arrange(new Rect(0, 0, size, size));
    }
    static void Draw(DrawingVisual visual, Action<DrawingContext, DateTime> render, DateTime time)
    {
        using var dc = visual.RenderOpen();
        render(dc, time);
    }
    static byte[] Pixels(DrawingVisual visual, double size, double dpi)
    {
        int pixels = (int)Math.Ceiling(size * dpi / 96);
        var bitmap = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] data = new byte[pixels * pixels * 4];
        bitmap.CopyPixels(data, pixels * 4, 0);
        return data;
    }
}
