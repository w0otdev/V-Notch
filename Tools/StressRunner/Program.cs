using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VNotch;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--check-unlock"))
        {
            // STA-only initialization check: no Application, Window or session event.
            var feedback = new VNotch.Controls.SessionUnlockFeedback();
            var geometryFactory = typeof(VNotch.Controls.SessionUnlockFeedback).GetMethod(
                "CreateLockGeometry", BindingFlags.Static | BindingFlags.NonPublic)!;
            for (int frame = 0; frame <= 20; frame++)
            {
                var geometry = (GeometryGroup)geometryFactory.Invoke(null, new object[] { frame / 20.0 })!;
                var shackleBounds = geometry.Children[1].Bounds;
                if (shackleBounds.IsEmpty || !double.IsFinite(shackleBounds.Width) || shackleBounds.Width < 1.9)
                    throw new InvalidOperationException("Shackle loses thickness during rotation.");
            }
            feedback.Play();
            feedback.Stop();
            feedback.Play();
            feedback.Stop();
            if (feedback.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Unlock feedback did not reset.");
            Console.WriteLine("Unlock feedback initialization and replay/reset passed; no window opened.");
            return;
        }
        if (args.Contains("--check-overload"))
        {
            OverloadChecks.Run();
            return;
        }
        if (args.Contains("--burn"))
        {
            var until = Stopwatch.StartNew();
            Parallel.For(0, Math.Min(8, Environment.ProcessorCount / 2), i =>
            {
                var data = new byte[16 * 1024 * 1024];
                int n = i;
                while (until.Elapsed.TotalSeconds < 32)
                {
                    for (int p = 0; p < data.Length; p += 64) data[p] ^= (byte)++n;
                    Thread.SpinWait(1000);
                }
                GC.KeepAlive(data);
            });
            return;
        }
        var app = new StressApp(args.Length > 0 ? args[0] : "default", Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/fps-stress/default"));
        app.Resources["SFProDisplay"] = new FontFamily("pack://application:,,,/V-Notch;component/Fonts/#SF Pro Display, Nirmala UI, Segoe UI");
        app.Resources["SFProText"] = app.Resources["SFProDisplay"];
        app.Resources["IconFont"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");
        app.Run();
    }
}

internal sealed class NoStartupChanges : IStartupManager
{
    public bool IsAutoStartEnabled() => false;
    public void SetAutoStart(bool enable) { }
}

internal sealed class StressApp(string style, string output) : Application
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<object> _summaries = new();
    private readonly List<double> _frames = new();
    private readonly List<double> _latencies = new();
    private readonly List<object> _errors = new();
    private readonly List<BitmapImage> _artworks = new();
    private MainWindow _window = null!;
    private ShellViewModel _vm = null!;
    private SettingsService _settings = null!;
    private string _phase = "startup";
    private long _lastFrame;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private long _heartbeat = Stopwatch.GetTimestamp();
    private long _mediaSubmitted, _mediaProcessed, _transitionsSubmitted, _transitionsProcessed, _rejected;
    private int _pending, _peakPending;
    private volatile bool _done;
    private volatile bool _stopLoad;
    private Action<string> _view = null!;
    private Action<MediaInfo> _media = null!;
    private Action _cleanup = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "pid.txt"), Environment.ProcessId.ToString());
        DispatcherUnhandledException += (_, error) =>
        {
            RecordError("DispatcherUnhandledException", error.Exception);
            File.WriteAllText(Path.Combine(output, "FAILED.txt"), error.Exception.ToString());
            // Preserve failure semantics; do not continue after an unhandled UI exception.
        };
        TaskScheduler.UnobservedTaskException += (_, error) => RecordError("UnobservedTask", error.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => RecordError("UnhandledException", error.ExceptionObject as Exception ?? new Exception(error.ExceptionObject.ToString()));
        try
        {
            RuntimeLog.InitializeNewSession(Path.Combine(output, "runtime.log"));
            RuntimeLog.MinimumLevel = LogLevel.Info;
            var config = new NotchSettings
            {
                NotchStyle = style, AutoStart = false, EnableLocalOnlyMode = true, AutoCheckUpdates = false,
                EnableOnlineArtworkLookup = false, EnableOnlineLyrics = false, EnableBrowserUrlInspection = false,
                EnableSpotifyCanvas = false, EnableSpotifyLyrics = false, EnableYouTubeSubtitles = false,
                EnableWeather = false, EnableHelloGreeting = false, EnableSpotlight = false,
                EnableSmartCrop = false, EnableHoverExpand = false, DisableMouseLeaveAutoClose = true,
                EnableDebugMode = false, AnimationFps = 240, AutoAnimationFps = true
            };
            _settings = (SettingsService)Activator.CreateInstance(typeof(SettingsService), PrivateInstance, null,
                new object?[] { Path.Combine(output, "settings.json"), null }, null)!;
            _settings.SaveAsync(config).GetAwaiter().GetResult();
            var services = new ServiceCollection();
            typeof(App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { services });
            services.RemoveAll<ISettingsService>();
            services.AddSingleton<ISettingsService>(_settings);
            services.RemoveAll<IStartupManager>();
            services.AddSingleton<IStartupManager, NoStartupChanges>();
            var provider = services.BuildServiceProvider();
            typeof(App).GetMethod("SetServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { provider });
            _window = provider.GetRequiredService<MainWindow>();
            _vm = provider.GetRequiredService<ShellViewModel>();
            _view = typeof(MainWindow).GetMethod("SetDebugViewState", PrivateInstance)!.CreateDelegate<Action<string>>(_window);
            _media = typeof(MainWindow).GetMethod("OnMediaChanged", PrivateInstance)!.CreateDelegate<Action<MediaInfo>>(_window);
            _cleanup = typeof(MainWindow).GetMethod("CleanupBeforeShutdown", PrivateInstance)!.CreateDelegate<Action>(_window);
            typeof(MainWindow).GetMethod("SetDebugViewLock", PrivateInstance)!.Invoke(_window, new object[] { true });
            MakeArtworks();
            _window.Show();
            CompositionTarget.Rendering += OnRendering;
            _ = Task.Run(Monitor);
            _ = RunPhases();
        }
        catch (Exception ex)
        {
            RecordError("Startup", ex);
            File.WriteAllText(Path.Combine(output, "FAILED.txt"), ex.ToString());
            Shutdown(2);
        }
    }

    private async Task RunPhases()
    {
        try
        {
            await Task.Delay(8000);
            App.Services.GetRequiredService<IMediaDetectionService>().Stop();
            ApplyMedia(0);
            _view("MediaExpanded");
            await Task.Delay(2000);
            await Phase("baseline-active", 15, false, 0, false);
            await Phase("transition-50Hz", 30, true, 0, false);
            await Phase("media-1000Hz", 30, false, 10, false);
            await Phase("mixed-burst", 30, true, 10, true);
            var currentSettings = (NotchSettings)typeof(MainWindow).GetField("_settings", PrivateInstance)!.GetValue(_window)!;
            currentSettings.NotchStyle = style;
            typeof(MainWindow).GetMethod("ApplySettings", PrivateInstance, null, new[] { typeof(bool) }, null)!.Invoke(_window, new object[] { false });
            using (var burn = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--burn") { UseShellExecute = false, CreateNoWindow = true }))
                await Phase("cpu-contention", 30, true, 2, false);
            _view("MediaExpanded");
            ApplyMedia(100_000);
            await Phase("recovery-active", 30, false, 0, false);
            _view("CollapsedNotch");
            await Phase("recovery-collapsed", 15, false, 0, false);
            _view("MediaExpanded");
            ApplyMedia(100_001);
            await Phase("steady-playing", 15, false, 0, false);
            CompositionTarget.Rendering -= OnRendering;
            _done = true;
            WriteResults();
            _cleanup();
            await _settings.DisposeAsync();
            File.WriteAllText(Path.Combine(output, "COMPLETED.txt"), DateTimeOffset.Now.ToString("O"));
            Shutdown();
        }
        catch (Exception ex)
        {
            RecordError("Scenario", ex);
            _done = true;
            WriteResults();
            File.WriteAllText(Path.Combine(output, "FAILED.txt"), ex.ToString());
            Shutdown(3);
        }
    }

    private async Task Phase(string name, int seconds, bool transitions, int mediaBatch, bool lifecycle)
    {
        _phase = name;
        _stopLoad = false;
        long started = Stopwatch.GetTimestamp();
        lock (_gate) { _frames.Clear(); _latencies.Clear(); _lastFrame = 0; }
        var startCounters = new[] { _mediaSubmitted, _mediaProcessed, _transitionsSubmitted, _transitionsProcessed, _rejected };
        Console.WriteLine($"PHASE {name} {DateTimeOffset.Now:O}");
        File.AppendAllText(Path.Combine(output, "phases.jsonl"), JsonSerializer.Serialize(new { name, utc = DateTimeOffset.UtcNow, qpc = started, seconds }) + "\n");
        await Task.Run(async () =>
        {
            int tick = 0;
            string[] views = { "MediaExpanded", "CollapsedNotch", "SecondaryShelf", "TimerStopwatch", "AudioRouting", "CompactMusicPill" };
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds && !_done && !_stopLoad)
            {
                int index = tick++;
                if (transitions && index % 2 == 0)
                {
                    Interlocked.Increment(ref _transitionsSubmitted);
                    Post(() => { _view(views[(index / 2) % views.Length]); Interlocked.Increment(ref _transitionsProcessed); });
                }
                for (int j = 0; j < mediaBatch; j++)
                {
                    int n = (int)Interlocked.Increment(ref _mediaSubmitted);
                    Post(() => { ApplyMedia(n); Interlocked.Increment(ref _mediaProcessed); });
                }
                // Theme lifecycle churn uses the production controller start/stop path.
                if (lifecycle && index % 20 == 0)
                    Post(() =>
                    {
                        var settings = (NotchSettings)typeof(MainWindow).GetField("_settings", PrivateInstance)!.GetValue(_window)!;
                        settings.NotchStyle = settings.NotchStyle == "liquidglass" ? "default" : "liquidglass";
                        typeof(MainWindow).GetMethod("ApplySettings", PrivateInstance, null, new[] { typeof(bool) }, null)!.Invoke(_window, new object[] { false });
                    });
                await Task.Delay(10);
            }
        });
        // Fence on the dispatcher ensures queued workload has drained before the phase ends.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        lock (_gate)
        {
            var frame = _frames.Order().ToArray();
            var latency = _latencies.Order().ToArray();
            _summaries.Add(new
            {
                name, seconds = elapsed, loadStoppedByWatchdog = _stopLoad, frames = frame.Length, callbackFps = frame.Length / elapsed,
                frameP50 = Percentile(frame, .5), frameP95 = Percentile(frame, .95), frameP99 = Percentile(frame, .99), frameMax = frame.LastOrDefault(),
                gapsOver33ms = frame.Count(x => x > 33.333), gapsOver100ms = frame.Count(x => x > 100),
                dispatcherP95 = Percentile(latency, .95), dispatcherMax = latency.LastOrDefault(),
                mediaSubmitted = _mediaSubmitted - startCounters[0], mediaProcessed = _mediaProcessed - startCounters[1],
                transitionRequests = _transitionsSubmitted - startCounters[2], transitionCalls = _transitionsProcessed - startCounters[3],
                droppedByHarness = _rejected - startCounters[4]
            });
        }
        WriteResults();
    }

    private void Post(Action action)
    {
        if (Interlocked.Increment(ref _pending) > 2000) { Interlocked.Decrement(ref _pending); Interlocked.Increment(ref _rejected); return; }
        _peakPending = Math.Max(_peakPending, _pending);
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() => { try { action(); } finally { Interlocked.Decrement(ref _pending); } }));
    }

    private void ApplyMedia(int n)
    {
        var info = new MediaInfo
        {
            CurrentTrack = $"Stress track {n / 20}", CurrentArtist = "Synthetic local workload", MediaSource = "Browser",
            SessionInstanceKey = "stress", IsPlaying = n % 100 != 0, IsAnyMediaPlaying = true,
            Position = TimeSpan.FromSeconds(n % 240), Duration = TimeSpan.FromSeconds(240), IsSeekEnabled = true,
            Thumbnail = _artworks[(n / 20) % _artworks.Count], LastUpdated = DateTimeOffset.Now
        };
        _vm.Media.Update(info);
        _vm.Progress.Update(info);
        _media(info);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastFrame != 0) _frames.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
            _lastFrame = now;
        }
    }

    private async Task Monitor()
    {
        using var proc = Process.GetCurrentProcess();
        double lastCpu = proc.TotalProcessorTime.TotalMilliseconds;
        long lastSample = Stopwatch.GetTimestamp();
        while (!_done)
        {
            await Task.Delay(1000);
            long now = Stopwatch.GetTimestamp();
            proc.Refresh();
            double cpu = proc.TotalProcessorTime.TotalMilliseconds;
            var row = new { utc = DateTimeOffset.UtcNow, phase = _phase, elapsed = _clock.Elapsed.TotalSeconds,
                cpuPercent = (cpu - lastCpu) / Stopwatch.GetElapsedTime(lastSample, now).TotalMilliseconds / Environment.ProcessorCount * 100,
                privateMiB = proc.PrivateMemorySize64 / 1048576.0, workingMiB = proc.WorkingSet64 / 1048576.0,
                managedMiB = GC.GetTotalMemory(false) / 1048576.0, handles = proc.HandleCount, threads = proc.Threads.Count,
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2), pending = _pending,
                uiAgeMs = Stopwatch.GetElapsedTime(Interlocked.Read(ref _heartbeat)).TotalMilliseconds,
                renderAgeMs = _lastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrame)).TotalMilliseconds };
            File.AppendAllText(Path.Combine(output, "telemetry.jsonl"), JsonSerializer.Serialize(row) + "\n");
            lastCpu = cpu; lastSample = now;
            if (row.uiAgeMs > 15000 && !_stopLoad)
            {
                _stopLoad = true;
                File.AppendAllText(Path.Combine(output, "overload.jsonl"), JsonSerializer.Serialize(row) + "\n");
            }
            if (row.privateMiB > 2048 || (row.uiAgeMs > 30000 && row.renderAgeMs > 15000) || _clock.Elapsed.TotalSeconds > 300)
            {
                File.WriteAllText(Path.Combine(output, "ABORTED.txt"), JsonSerializer.Serialize(row));
                Environment.Exit(4);
            }
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                long completed = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _heartbeat, completed);
                lock (_gate) _latencies.Add(Stopwatch.GetElapsedTime(now, completed).TotalMilliseconds);
            }));
        }
    }

    private void MakeArtworks()
    {
        var random = new Random(1234);
        for (int n = 0; n < 24; n++)
        {
            var bytes = new byte[256 * 256 * 4]; random.NextBytes(bytes);
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            var source = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, bytes, 256 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            _artworks.Add(image);
        }
    }

    private void RecordError(string kind, Exception ex)
    {
        lock (_gate) _errors.Add(new { kind, phase = _phase, error = ex.ToString() });
        File.AppendAllText(Path.Combine(output, "errors.txt"), kind + "\n" + ex + "\n");
    }
    private void WriteResults()
    {
        lock (_gate) File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        { style, pid = Environment.ProcessId, runtime = Environment.Version.ToString(), renderTier = RenderCapability.Tier >> 16,
            peakHarnessQueue = _peakPending, completed = _done, phases = _summaries, errors = _errors }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static double Percentile(double[] sorted, double percentile) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
}
