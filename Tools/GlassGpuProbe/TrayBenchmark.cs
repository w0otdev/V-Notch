using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;

internal static class TrayBenchmark
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double MiB = 1048576.0;
    private static object? Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);

    internal static int Prepare(string root, int maxEntries = 2000)
    {
        async Task PrepareAsync()
        {
            Directory.CreateDirectory(root);
            string inputs = Path.Combine(root, "inputs"); Directory.CreateDirectory(inputs);
            using var store = new ClipboardHistoryStore(Path.Combine(root, "store")) { MaxUnpinnedEntries = maxEntries };
            await store.InitializeAsync();
            for (int batch = 0; batch < 10; batch++)
            {
                var paths = new List<string>();
                for (int i = 0; i < 100; i++)
                {
                    string path = Path.Combine(inputs, $"sample-{batch * 100 + i:0000}.txt");
                    await File.WriteAllTextAsync(path, $"Synthetic benchmark document {batch * 100 + i}\n" + new string('x', 1024));
                    paths.Add(path);
                }
                await store.ImportAsync(new ClipboardCapture { FilePaths = paths.ToArray(), SeparateFiles = true });
            }
            for (int i = 0; i < 24; i++)
            {
                const int w = 1800, h = 1200;
                byte[] pixels = new byte[w * h * 4];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int offset = (y * w + x) * 4;
                    pixels[offset] = (byte)(x / 7 + i * 5); pixels[offset + 1] = (byte)(y / 5 + i * 7);
                    pixels[offset + 2] = (byte)((x + y) / 13 + i * 11); pixels[offset + 3] = 255;
                }
                var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream);
                await store.ImportAsync(new ClipboardCapture { ImagePng = stream.ToArray(), CopiedUtc = DateTime.UtcNow.AddMinutes(1).AddSeconds(i) });
            }
            Console.WriteLine($"Fixture ready: 1000 small text files + 24 images (1800x1200), root={root}");
        }
        try { PrepareAsync().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }

    internal static int Run(string root, int maxEntries = 2000, bool enableGpu = true)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["SFProDisplay"] = AppFonts.Get("Segoe UI");
        app.Resources["SFProText"] = AppFonts.Get("Segoe UI");
        app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        typeof(LiquidGlassController).Assembly.GetType("VNotch.Services.AnimationConfig")!
            .GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [120, false]);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Task<int> task = RunAsync(Path.GetFullPath(root), maxEntries, enableGpu);
        while (!task.IsCompleted) BenchmarkProbe.Pump(50);
        return task.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string root, int maxEntries, bool enableGpu)
    {
        var phases = new List<object>();
        string? failure = null;
        using var process = Process.GetCurrentProcess();
        using var store = new ClipboardHistoryStore(Path.Combine(root, "store")) { MaxUnpinnedEntries = maxEntries };
        ClipboardHistoryController? controller = null;
        ClipboardTray? tray = null;
        var window = new Window { Width = 900, Height = 420, Left = 160, Top = 120,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent };
        var content = new Grid { ClipToBounds = true };
        var canvas = new Canvas { ClipToBounds = true };
        var image = new Image { Stretch = Stretch.Fill };
        canvas.Children.Add(image); content.Children.Add(canvas); window.Content = content;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int width = (int)(900 * dpi), height = (int)(420 * dpi);
        using var glass = new LiquidGlassController(image, () => hwnd,
            () => new(200, 150, width, height), maxRegionWidth: width, maxRegionHeight: height)
            { CropGpuPresentationToCapture = true, HideFromScreenCapture = true };
        var wake = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(8) };
        wake.Tick += (_, _) => glass.ForceRefresh();
        var latencies = new List<double>(8192);
        var glassIntervals = new List<double>(8192);
        var uiIntervals = new List<double>(8192);
        bool recording = false;
        long previousUi = 0;
        long previousGlass = 0;
        Func<ulong> consumerRetries = () => 0;
        int uiFrames = 0;
        TimeSpan previousRenderingTime = TimeSpan.MinValue;
        void OnRendering(object? _, EventArgs args)
        {
            if (!recording || args is not RenderingEventArgs frame || frame.RenderingTime == previousRenderingTime) return;
            previousRenderingTime = frame.RenderingTime;
            long now = Stopwatch.GetTimestamp();
            if (previousUi != 0) uiIntervals.Add((now - previousUi) * 1000.0 / Stopwatch.Frequency);
            previousUi = now; uiFrames++;
        }
        async Task Until(Func<bool> ready, string description)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException(description);
                await Task.Delay(25);
            }
        }
        async Task Measure(string name, Func<Task> action)
        {
            process.Refresh(); long start = process.PrivateMemorySize64, peak = start, workingPeak = process.WorkingSet64;
            TimeSpan cpu = process.TotalProcessorTime;
            latencies.Clear(); glassIntervals.Clear(); uiIntervals.Clear(); previousUi = previousGlass = 0; uiFrames = 0;
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long allocated = GC.GetTotalAllocatedBytes(true);
            ulong consumerStart = consumerRetries();
            var sampler = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            sampler.Tick += (_, _) => { process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64); workingPeak = Math.Max(workingPeak, process.WorkingSet64); };
            bool observeUi = name != "settled-idle";
            if (observeUi) CompositionTarget.Rendering += OnRendering;
            BenchmarkEvents.Log.PhaseStart(name, 0);
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew(); recording = true; sampler.Start();
            try { await action(); }
            finally { recording = false; sampler.Stop(); CompositionTarget.Rendering -= OnRendering; }
            double seconds = watch.Elapsed.TotalSeconds;
            BenchmarkEvents.Log.PhaseStop(name, 0);
            ulong consumerDelta = consumerRetries() - consumerStart;
            long deltaAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            int gen0 = GC.CollectionCount(0) - gc0, gen1 = GC.CollectionCount(1) - gc1, gen2 = GC.CollectionCount(2) - gc2;
            process.Refresh();
            latencies.Sort(); glassIntervals.Sort(); uiIntervals.Sort();
            double Q(List<double> values, double q) => values.Count == 0 ? 0 : values[(int)Math.Ceiling((values.Count - 1) * q)];
            var cards = tray == null ? Array.Empty<object>() : ((IEnumerable)Field(tray, "_cards")!).Cast<object>().ToArray();
            int previews = cards.Count(card => card.GetType().GetProperty("Image")!.GetValue(card) != null);
            double previewMiB = (long)Field(store, "_imagePreviewBytes")! / MiB;
            double privateDeltaMiB = (process.PrivateMemorySize64 - start) / MiB;
            double glassIntervalP95 = Q(glassIntervals, .95);
            double latePercent = glassIntervals.Count == 0 ? 0 : glassIntervals.Count(v => v > 12.5) * 100.0 / glassIntervals.Count;
            bool sustained = name.StartsWith("image-scroll-") || name == "file-scroll-50-viewports";
            var gateFailures = new List<string>();
            if (enableGpu && sustained) gateFailures.AddRange(BenchmarkChecks.Cadence(120, latencies.Count / seconds, glassIntervalP95, latePercent));
            if (previewMiB > 8.000001) gateFailures.Add("Preview cache exceeds 8 MiB");
            bool warmImages = name is "image-scroll-2" or "image-scroll-3";
            if (warmImages && gen2 > 1) gateFailures.Add("Warm image sweep has more than one full GC");
            if ((warmImages || name == "reopen-10-cycles") && privateDeltaMiB > 8) gateFailures.Add("Warm workload grows private commit by more than 8 MiB");
            double cpuPct = (process.TotalProcessorTime - cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            var result = new { Name = name, StartedUtc = startedUtc, Seconds = seconds, PrivateStartMiB = start / MiB,
                PrivateEndMiB = process.PrivateMemorySize64 / MiB, PrivateDeltaMiB = privateDeltaMiB,
                PrivatePeakMiB = Math.Max(peak, process.PrivateMemorySize64) / MiB, WorkingSetPeakMiB = Math.Max(workingPeak, process.WorkingSet64) / MiB,
                ManagedAllocatedMiB = deltaAllocated / MiB, ManagedMiBPerSecond = deltaAllocated / MiB / seconds,
                ManagedHeapMiB = GC.GetTotalMemory(false) / MiB,
                Gen0 = gen0, Gen1 = gen1, Gen2 = gen2,
                CpuMachinePercent = cpuPct, UiFps = uiFrames / seconds, UiIntervalP95Ms = Q(uiIntervals, .95),
                GlassFps = latencies.Count / seconds, GlassLatencyP95Ms = Q(latencies, .95),
                GlassIntervalP95Ms = glassIntervalP95, GlassLateIntervalsPercent = latePercent,
                ConsumerRetries = consumerDelta, UiRenderingObserved = observeUi,
                GatesPassed = gateFailures.Count == 0, GateFailures = gateFailures,
                Cards = cards.Length, RetainedCardPreviews = previews,
                PreviewCacheMiB = previewMiB,
                PreviewCacheCount = ((IDictionary)Field(store, "_imagePreviews")!).Count };
            phases.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        try
        {
            await Measure("metadata-load", async () => await store.InitializeAsync());
            controller = new ClipboardHistoryController(window.Dispatcher, store) { IsPaused = true };
            if (enableGpu)
            {
                if (!glass.SetGpuMode(true, g =>
                {
                    image.Width = g.SrcW / dpi; image.Height = g.SrcH / dpi;
                    Canvas.SetLeft(image, -g.OffX / dpi); Canvas.SetTop(image, -g.OffY / dpi);
                }, ex => failure = ex.ToString(), ultimateGpu: true)) throw new InvalidOperationException("GPU unavailable");
                typeof(LiquidGlassController).GetMethod("EnableNativeRefraction", Private)!.Invoke(glass, null);
                var presenter = Field(glass, "_d3dPresenter")!;
                consumerRetries = (Func<ulong>)presenter.GetType().GetProperty("GpuConsumerRetries", Private)!.GetMethod!.CreateDelegate(typeof(Func<ulong>), presenter);
                var latency = (Func<double>)typeof(LiquidGlassController).GetProperty("LastSubmissionLatencyMs", Private)!.GetMethod!.CreateDelegate(typeof(Func<double>), glass);
                presenter.GetType().GetEvent("FramePresented")!.AddEventHandler(presenter, (Action<LiquidGlassController.GpuGeometry>)(_ =>
                {
                    if (!recording) return;
                    long now = Stopwatch.GetTimestamp();
                    latencies.Add(latency());
                    if (previousGlass != 0) glassIntervals.Add((now - previousGlass) * 1000.0 / Stopwatch.Frequency);
                    previousGlass = now;
                }));
                glass.SetBlur(28 * .05 * .05 * dpi); glass.UpdateFps(120); glass.SetAnimating(true);
                window.Show(); glass.Start(); wake.Start();
                await Until(() => glass.HasPresentedFrame || failure != null, "first native glass frame");
            }
            else { window.Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)); window.Show(); }
            if (failure != null) throw new InvalidOperationException(failure);
            await Task.Delay(700);
            int expectedInitial = Math.Min(maxEntries, 1024);
            await Measure($"first-open-{expectedInitial}-cards", async () =>
            {
                tray = new ClipboardTray(); content.Children.Add(tray); tray.Attach(controller);
                tray.BeginEntrance();
                await Until(() => ((IList)Field(tray, "_cards")!).Count == expectedInitial, $"{expectedInitial} cards loaded");
                await Until(() => ((IDictionary)Field(tray, "_thumbnailLoads")!).Count == 0, "initial previews completed");
                await Task.Delay(500);
            });
            var list = (ListBox)tray!.FindName("Cards");
            list.ApplyTemplate();
            ScrollViewer FindScroll(DependencyObject parent)
            {
                if (parent is ScrollViewer scroll) return scroll;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
                return null!;
            }
            var scroll = FindScroll(list) ?? throw new InvalidOperationException("No card scroll viewer");
            async Task Sweep(double distance, int steps)
            {
                for (int i = 0; i <= steps; i++) { scroll.ScrollToHorizontalOffset(distance * i / steps); await Task.Delay(80); }
                await Until(() => ((IDictionary)Field(tray, "_thumbnailLoads")!).Count == 0, "scroll previews complete");
            }
            for (int repeat = 1; repeat <= 3; repeat++)
            {
                scroll.ScrollToHorizontalOffset(0); await Task.Delay(300);
                await Measure($"image-scroll-{repeat}", () => Sweep(6000, 60));
            }
            await Measure("file-scroll-50-viewports", () => Sweep(scroll.ScrollableWidth, 50));
            await Measure("reopen-10-cycles", async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    tray.Visibility = Visibility.Collapsed; await Task.Delay(120);
                    tray.BeginEntrance(); tray.Visibility = Visibility.Visible; await Task.Delay(600);
                }
            });
            for (int batch = 0; batch < 3; batch++)
            {
                var paths = new List<string>();
                for (int i = 0; i < 100; i++)
                {
                    string path = Path.Combine(root, "inputs", $"added-{batch * 100 + i}.txt");
                    await File.WriteAllTextAsync(path, $"Synthetic added document {batch * 100 + i}"); paths.Add(path);
                }
                await Measure($"append-100-files-{batch + 1}", async () =>
                {
                    var imported = await store.ImportAsync(new ClipboardCapture { FilePaths = paths.ToArray(), SeparateFiles = true });
                    Guid? firstId = imported.Entries.FirstOrDefault()?.Id ?? imported.Entry?.Id;
                    await Until(() => list.Items.Count == Math.Min(maxEntries, expectedInitial + (batch + 1) * 100) &&
                        list.Items.Cast<VNotch.ViewModels.ClipboardCardViewModel>().Any(card => card.Entry.Id == firstId), "appended cards visible");
                    await Task.Delay(400);
                });
            }
            glass.SetAnimating(false); glass.SetIdle(true); wake.Stop(); await Task.Delay(1200);
            await Measure("settled-idle", () => Task.Delay(5000));
            if (failure != null) throw new InvalidOperationException(failure);
            return 0;
        }
        catch (Exception ex) { failure = ex.ToString(); Console.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            recording = false; CompositionTarget.Rendering -= OnRendering;
            wake.Stop(); glass.Stop(); window.Close(); tray?.Dispose();
            if (controller != null) await controller.DisposeAsync();
            string output = BenchmarkProbe.ResultPath(!enableGpu ? "tray-benchmark-solid-results.json" :
                maxEntries == 500 ? "tray-benchmark-default-results.json" : "tray-benchmark-results.json");
            File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow,
                Root = root, DpiScale = dpi, TargetFps = 120, BlurPercent = enableGpu ? 5 : 0,
                Backdrop = enableGpu ? "liquidglass" : "solid", MaxUnpinnedEntries = maxEntries, LogicalProcessors = Environment.ProcessorCount,
                Note = "Real ClipboardTray + isolated store. Fixture generated in a separate process. Up to 1000 small text files + 24 1800x1200 images; retention matches configured cap; three appended batches. Includes harness overhead; no forced GC. Not the full application.",
                Gates = "Sustained glass phases use the fixed cadence gate. Cache <=8MiB; warm image sweeps <=1 Gen2 and <=8MiB private growth; ten reopens <=8MiB private growth. Cold opening is observed, not hidden by a median. Idle has no rendering observer.",
                Failure = failure, Phases = phases }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Results: " + output);
        }
    }
}
