using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
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
    internal static bool ObserveUi { get; set; } = true;
    internal static bool MoveBackdrop { get; set; }
    internal static bool ProfileUi { get; set; }
    internal static bool WheelScroll { get; set; }
    internal static bool ControlledBackdrop { get; set; }
    internal static bool ResizeTray { get; set; }
    internal static bool OpaqueHost { get; set; }
    internal static bool DwmHost { get; set; }
    internal static bool ForegroundHost { get; set; }
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
        Window? backdrop = null;
        var window = new Window { Width = 900, Height = 420, Left = 160, Top = 120,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Topmost = ForegroundHost,
            AllowsTransparency = !OpaqueHost && !DwmHost, Background = OpaqueHost ? Brushes.Black : Brushes.Transparent };
        var content = new Grid { ClipToBounds = true };
        var canvas = new Canvas { ClipToBounds = true };
        var image = new Image { Stretch = Stretch.Fill };
        canvas.Children.Add(image); content.Children.Add(canvas); window.Content = content;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (DwmHost) DwmProbeHost.Enable(hwnd);
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
        var scrollIntervals = new List<double>(8192);
        int scrollFrames = 0;
        long previousScroll = 0;
        bool recording = false;
        var uiOperations = new List<object>();
        var operationStarts = new Dictionary<DispatcherOperation, long>();
        var operationMethod = typeof(DispatcherOperation).GetField("_method", Private);
        void OperationStarted(object? sender, DispatcherHookEventArgs args)
        {
            if (recording) operationStarts[args.Operation] = Stopwatch.GetTimestamp();
        }
        void OperationCompleted(object? sender, DispatcherHookEventArgs args)
        {
            if (!operationStarts.Remove(args.Operation, out long began) || !recording) return;
            long ended = Stopwatch.GetTimestamp();
            double milliseconds = (ended - began) * 1000.0 / Stopwatch.Frequency;
            if (milliseconds < 2) return;
            var method = (operationMethod?.GetValue(args.Operation) as Delegate)?.Method;
            uiOperations.Add(new { Ms = milliseconds, Priority = args.Operation.Priority.ToString(),
                Method = method == null ? "unknown" : method.DeclaringType?.FullName + "." + method.Name });
        }
        var gpuRenderTimes = new List<double>(8192);
        var gpuWaitTimes = new List<double>(8192);
        var handoffTimes = new List<double>(8192);
        long previousUi = 0;
        long previousGlass = 0;
        Func<ulong> consumerRetries = () => 0;
        Func<long> imageRetries = () => 0, gpuRetries = () => 0, workerTimeouts = () => 0;
        Func<long> captureRequests = () => 0, renderSkips = () => 0;
        Func<double> gpuRender = () => 0, gpuWait = () => 0;
        bool moveBackdrop = MoveBackdrop;
        int backdropPhase = 0;
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
            scrollIntervals.Clear(); scrollFrames = 0; previousScroll = 0;
            uiOperations.Clear(); operationStarts.Clear();
            if (ProfileUi)
            {
                window.Dispatcher.Hooks.OperationStarted += OperationStarted;
                window.Dispatcher.Hooks.OperationCompleted += OperationCompleted;
            }
            gpuRenderTimes.Clear(); gpuWaitTimes.Clear(); handoffTimes.Clear();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long allocated = GC.GetTotalAllocatedBytes(true);
            ulong consumerStart = consumerRetries();
            long imageRetryStart = imageRetries(), gpuRetryStart = gpuRetries(), timeoutStart = workerTimeouts();
            long captureStart = captureRequests(), skipStart = renderSkips();
            var sampler = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            sampler.Tick += (_, _) =>
            {
                var memory = BenchmarkChecks.ReadMemory();
                peak = Math.Max(peak, memory.PrivateBytes); workingPeak = Math.Max(workingPeak, memory.WorkingSet);
            };
            bool observeUi = ObserveUi && name != "settled-idle";
            if (observeUi) CompositionTarget.Rendering += OnRendering;
            BenchmarkEvents.Log.PhaseStart(name, 0);
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            double startedQpcMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
            var watch = Stopwatch.StartNew(); recording = true; sampler.Start();
            try { await action(); }
            finally
            {
                recording = false; sampler.Stop(); CompositionTarget.Rendering -= OnRendering;
                window.Dispatcher.Hooks.OperationStarted -= OperationStarted;
                window.Dispatcher.Hooks.OperationCompleted -= OperationCompleted;
            }
            double seconds = watch.Elapsed.TotalSeconds;
            BenchmarkEvents.Log.PhaseStop(name, 0);
            ulong consumerDelta = consumerRetries() - consumerStart;
            long deltaAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            int gen0 = GC.CollectionCount(0) - gc0, gen1 = GC.CollectionCount(1) - gc1, gen2 = GC.CollectionCount(2) - gc2;
            process.Refresh();
            latencies.Sort(); glassIntervals.Sort(); uiIntervals.Sort();
            scrollIntervals.Sort();
            gpuRenderTimes.Sort(); gpuWaitTimes.Sort(); handoffTimes.Sort();
            double Q(List<double> values, double q) => values.Count == 0 ? 0 : values[(int)Math.Ceiling((values.Count - 1) * q)];
            var cards = tray == null ? Array.Empty<object>() : ((IEnumerable)Field(tray, "_cards")!).Cast<object>().ToArray();
            int previews = cards.Count(card => card.GetType().GetProperty("Image")!.GetValue(card) != null);
            double previewMiB = (long)Field(store, "_imagePreviewBytes")! / MiB;
            double privateDeltaMiB = (process.PrivateMemorySize64 - start) / MiB;
            double glassIntervalP95 = Q(glassIntervals, .95);
            double latePercent = glassIntervals.Count == 0 ? 0 : glassIntervals.Count(v => v > 12.5) * 100.0 / glassIntervals.Count;
            bool sustained = name.StartsWith("image-scroll-") || name is "file-scroll-50-viewports" or "tray-resize-250ms";
            var gateFailures = new List<string>();
            if (enableGpu && sustained) gateFailures.AddRange(BenchmarkChecks.Cadence(120, latencies.Count / seconds, glassIntervalP95, latePercent));
            if (previewMiB > 8.000001) gateFailures.Add("Preview cache exceeds 8 MiB");
            bool warmImages = name is "image-scroll-2" or "image-scroll-3";
            if (warmImages && gen2 > 1) gateFailures.Add("Warm image sweep has more than one full GC");
            if ((warmImages || name == "reopen-10-cycles") && privateDeltaMiB > 8) gateFailures.Add("Warm workload grows private commit by more than 8 MiB");
            double cpuPct = (process.TotalProcessorTime - cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
            var result = new { Name = name, StartedUtc = startedUtc, StartedQpcMs = startedQpcMs, Seconds = seconds, PrivateStartMiB = start / MiB,
                PrivateEndMiB = process.PrivateMemorySize64 / MiB, PrivateDeltaMiB = privateDeltaMiB,
                PrivatePeakMiB = Math.Max(peak, process.PrivateMemorySize64) / MiB, WorkingSetPeakMiB = Math.Max(workingPeak, process.WorkingSet64) / MiB,
                ManagedAllocatedMiB = deltaAllocated / MiB, ManagedMiBPerSecond = deltaAllocated / MiB / seconds,
                ManagedHeapMiB = GC.GetTotalMemory(false) / MiB,
                Gen0 = gen0, Gen1 = gen1, Gen2 = gen2,
                CpuMachinePercent = cpuPct, UiFps = uiFrames / seconds, UiIntervalP95Ms = Q(uiIntervals, .95),
                ScrollFps = scrollFrames / seconds, ScrollIntervalP95Ms = Q(scrollIntervals, .95),
                GlassFps = latencies.Count / seconds, GlassLatencyP95Ms = Q(latencies, .95),
                GpuRenderP95Ms = Q(gpuRenderTimes, .95), GpuWaitP95Ms = Q(gpuWaitTimes, .95),
                UiHandoffP95Ms = Q(handoffTimes, .95), ImageRetries = imageRetries() - imageRetryStart,
                CaptureRequests = captureRequests() - captureStart, RenderSkips = renderSkips() - skipStart,
                GpuRetries = gpuRetries() - gpuRetryStart, WorkerTimeouts = workerTimeouts() - timeoutStart,
                GlassIntervalP95Ms = glassIntervalP95, GlassLateIntervalsPercent = latePercent,
                ConsumerRetries = consumerDelta, UiRenderingObserved = observeUi,
                GatesPassed = gateFailures.Count == 0, GateFailures = gateFailures,
                Cards = cards.Length, RetainedCardPreviews = previews,
                PreviewCacheMiB = previewMiB,
                PreviewCacheCount = ((IDictionary)Field(store, "_imagePreviews")!).Count };
            if (ProfileUi) Console.WriteLine(JsonSerializer.Serialize(new { Phase = name, UiOperations = uiOperations }));
            phases.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        try
        {
            if (ControlledBackdrop)
            {
                var drawing = new GeometryDrawing(new LinearGradientBrush(Colors.SteelBlue, Colors.DarkGoldenrod, 0),
                    new Pen(Brushes.White, 2), new RectangleGeometry(new Rect(0, 0, 32, 32)));
                var pattern = new DrawingBrush(drawing) { TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 32, 32) };
                pattern.Freeze();
                backdrop = new Window { Left = 0, Top = 0, Width = (200 + width + 512) / dpi,
                    Height = (150 + height + 512) / dpi, ShowActivated = false, ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = pattern };
                backdrop.Topmost = ForegroundHost;
                backdrop.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
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
                Func<T> Getter<T>(string name) => (Func<T>)presenter.GetType().GetProperty(name, Private)!.GetMethod!.CreateDelegate(typeof(Func<T>), presenter);
                imageRetries = Getter<long>("ImageBusyRetries"); gpuRetries = Getter<long>("GpuCompletionRetries");
                workerTimeouts = Getter<long>("GpuWorkerTimeouts");
                gpuRender = Getter<double>("SubmittedGpuRenderMs"); gpuWait = Getter<double>("SubmittedGpuWaitMs");
                captureRequests = Getter<long>("GpuCaptureRequests"); renderSkips = Getter<long>("GpuRenderSkips");
                consumerRetries = (Func<ulong>)presenter.GetType().GetProperty("GpuConsumerRetries", Private)!.GetMethod!.CreateDelegate(typeof(Func<ulong>), presenter);
                var latency = (Func<double>)typeof(LiquidGlassController).GetProperty("LastSubmissionLatencyMs", Private)!.GetMethod!.CreateDelegate(typeof(Func<double>), glass);
                presenter.GetType().GetEvent("FramePresented")!.AddEventHandler(presenter, (Action<LiquidGlassController.GpuGeometry>)(_ =>
                {
                    if (recording)
                    {
                        long now = Stopwatch.GetTimestamp();
                        double total = latency(), render = gpuRender(), wait = gpuWait();
                        latencies.Add(total); gpuRenderTimes.Add(render); gpuWaitTimes.Add(wait);
                        handoffTimes.Add(Math.Max(0, total - render - wait));
                        if (previousGlass != 0) glassIntervals.Add((now - previousGlass) * 1000.0 / Stopwatch.Frequency);
                        previousGlass = now;
                    }
                    if (moveBackdrop)
                    {
                        backdropPhase = (backdropPhase + 1) % 8;
                        glass.SetLiveRegion(new(200 + backdropPhase, 150, width, height));
                    }
                }));
                glass.SetBlur(28 * .05 * .05 * dpi); glass.UpdateFps(120); glass.SetAnimating(true);
                window.Show();
                if (ForegroundHost) { window.Topmost = false; window.Topmost = true; }
                glass.Start(); wake.Start();
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
            if (WheelScroll)
                typeof(LiquidGlassController).Assembly.GetType("VNotch.Services.AnimationConfig")!
                    .GetMethod("Configure")!.Invoke(null, [120, false]);
            scroll.ScrollChanged += (_, args) =>
            {
                if (!recording || Math.Abs(args.HorizontalChange) < .01) return;
                long now = Stopwatch.GetTimestamp();
                if (previousScroll != 0) scrollIntervals.Add((now - previousScroll) * 1000.0 / Stopwatch.Frequency);
                previousScroll = now; scrollFrames++;
            };
            var wheel = (Action<object, MouseWheelEventArgs>)typeof(ClipboardTray).GetMethod("Cards_Wheel", Private)!
                .CreateDelegate(typeof(Action<object, MouseWheelEventArgs>), tray);
            var target = typeof(ClipboardTray).GetField("_cardsScrollTarget", Private)!;
            var scrollActive = typeof(ClipboardTray).GetField("_cardsScrollActive", Private)!;
            var stopScroll = (Action)typeof(ClipboardTray).GetMethod("StopCardsScrollFrames", Private)!.CreateDelegate(typeof(Action), tray);
            async Task Sweep(double distance, int steps)
            {
                for (int i = 0; i <= steps; i++)
                {
                    double desired = distance * i / steps;
                    if (WheelScroll)
                    {
                        double current = (bool)scrollActive.GetValue(tray)! ? (double)target.GetValue(tray)! : scroll.HorizontalOffset;
                        wheel(list, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
                            (int)Math.Round((current - desired) / 1.25)) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                    }
                    else scroll.ScrollToHorizontalOffset(desired);
                    await Task.Delay(80);
                }
                await Until(() => ((IDictionary)Field(tray, "_thumbnailLoads")!).Count == 0, "scroll previews complete");
            }
            for (int repeat = 1; repeat <= 3; repeat++)
            {
                stopScroll();
                scroll.ScrollToHorizontalOffset(0); await Task.Delay(300);
                await Measure($"image-scroll-{repeat}", () => Sweep(6000, 60));
            }
            stopScroll();
            scroll.ScrollToHorizontalOffset(0); await Task.Delay(300);
            await Measure("file-scroll-50-viewports", () => Sweep(scroll.ScrollableWidth, 50));
            if (ResizeTray)
            {
                stopScroll();
                var setBounds = typeof(LiquidGlassController).Assembly.GetType("VNotch.Services.Win32Interop")!
                    .GetMethod("SetWindowPos", BindingFlags.Public | BindingFlags.Static)!
                    .CreateDelegate<Func<IntPtr, IntPtr, int, int, int, int, uint, bool>>();
                void Resize(bool compact)
                {
                    width = (int)Math.Round((compact ? 480 : 900) * dpi);
                    height = (int)Math.Round((compact ? 300 : 420) * dpi);
                    if (!setBounds(hwnd, IntPtr.Zero, 0, 0, width, height, 0x0016))
                        throw new InvalidOperationException("Cannot resize tray benchmark HWND");
                    glass.SetLiveRegion(new(200 + backdropPhase, 150, width, height));
                }
                var input = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
                int direction = 120;
                input.Tick += (_, _) =>
                {
                    if (scroll.HorizontalOffset < 150) direction = -120;
                    else if (scroll.HorizontalOffset > scroll.ScrollableWidth - 150) direction = 120;
                    wheel(list, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, direction)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                };
                await Measure("tray-resize-250ms", async () =>
                {
                    input.Start();
                    try { for (int i = 0; i < 40; i++) { Resize(i % 2 == 0); await Task.Delay(250); } }
                    finally { input.Stop(); stopScroll(); Resize(false); }
                });
                await Task.Delay(300);
            }
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
            moveBackdrop = false;
            glass.SetAnimating(false); glass.SetIdle(true); wake.Stop(); await Task.Delay(1200);
            await Measure("settled-idle", () => Task.Delay(5000));
            if (failure != null) throw new InvalidOperationException(failure);
            return 0;
        }
        catch (Exception ex) { failure = ex.ToString(); Console.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            bool nativeVisible = DwmProbeHost.IsWindowVisible(hwnd);
            recording = false; CompositionTarget.Rendering -= OnRendering;
            wake.Stop(); glass.Stop(); window.Close(); backdrop?.Close(); tray?.Dispose();
            if (controller != null) await controller.DisposeAsync();
            string output = BenchmarkProbe.ResultPath(!enableGpu ? "tray-benchmark-solid-results.json" :
                maxEntries == 500 ? "tray-benchmark-default-results.json" : "tray-benchmark-results.json");
            File.WriteAllText(output, JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow,
                Root = root, DpiScale = dpi, TargetFps = 120, BlurPercent = enableGpu ? 5 : 0,
                Backdrop = enableGpu ? "liquidglass" : "solid", MaxUnpinnedEntries = maxEntries, LogicalProcessors = Environment.ProcessorCount,
                MovingBackdrop = MoveBackdrop, UiObserverEnabled = ObserveUi,
                ControlledBackdrop = ControlledBackdrop,
                ResizeTray = ResizeTray,
                LayeredWindow = !OpaqueHost && !DwmHost,
                DwmHost = DwmHost,
                ForegroundHost = ForegroundHost,
                NativeWindowVisible = nativeVisible,
                WheelScrolling = WheelScroll, AnimationTargetFps = WheelScroll ? 120 : (int?)null,
                DispatcherHooksEnabled = ProfileUi,
                RenderingTier = RenderCapability.Tier >> 16,
                Note = "Real ClipboardTray + isolated store. Fixture generated in a separate process. Up to 1000 small text files + 24 1800x1200 images; retention matches configured cap; three appended batches. Includes harness overhead; no forced GC. Not the full application.",
                Gates = "Sustained glass phases use the fixed cadence gate. Cache <=8MiB; warm image sweeps <=1 Gen2 and <=8MiB private growth; ten reopens <=8MiB private growth. Cold opening is observed, not hidden by a median. Idle has no rendering observer.",
                Failure = failure, Phases = phases }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Results: " + output);
        }
    }
}
