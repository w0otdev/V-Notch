using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

internal static class MainWindowProbe
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    // Application queues OnStartup even without Run(). Keep the resource setup,
    // but never start the real tray app, show windows, or change user settings.
    private sealed class ProbeApplication : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
    internal static int Run()
    {
        var app = new ProbeApplication();
        app.Resources["SFProDisplay"] = AppFonts.Get("Segoe UI");
        app.Resources["SFProText"] = AppFonts.Get("Segoe UI");
        app.Resources["IconFont"] = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Console.WriteLine("Probe: application resources loaded.");
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-glass-probe-" + Guid.NewGuid().ToString("N"));
        var settings = (SettingsService)typeof(SettingsService).GetConstructor(Private, null,
            [typeof(string), typeof(Action<string>)], null)!.Invoke(
                [System.IO.Path.Combine(directory, "settings.json"), (Action<string>)(_ => { })]);
        settings.Save(new NotchSettings
        {
            NotchStyle = "liquidglass", Width = 300, Height = 40,
            LiquidGlass = new LiquidGlassConfig { BlurAmount = .05 },
            EnableLocalOnlyMode = true, EnableHelloGreeting = false,
            EnableSpotlight = false, EnableWeather = false, AutoCheckUpdates = false,
            HideOnExclusiveFullscreen = false, HideOnWindowedFullscreen = false
        });
        var services = new ServiceCollection();
        Console.WriteLine("Probe: isolated settings created.");
        ServiceConfigurator.ConfigureServices(services);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton(new VNotch.Services.Clipboard.ClipboardHistoryStore(
            System.IO.Path.Combine(directory, "Clipboard")));
        using var provider = services.BuildServiceProvider();
        Console.WriteLine("Probe: services registered.");
        var window = provider.GetRequiredService<MainWindow>();
        Console.WriteLine("Probe: MainWindow constructed.");
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            object shellState = typeof(MainWindow).GetField("_shellState", Private)!.GetValue(window)!;
            shellState.GetType().GetProperty("Hwnd")!.SetValue(shellState, hwnd);
            Console.WriteLine("Probe: HWND created.");
            typeof(MainWindow).GetProperty("_isNotchVisible", Private)!.SetValue(window, true);
            typeof(MainWindow).GetField("_gpuRefractionFailed", Private)!.SetValue(window, false);
            var notch = (FrameworkElement)window.FindName("NotchBorder");
            notch.Width = 300;
            notch.Height = 40;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1000, 700));
            content.Arrange(new Rect(0, 0, 1000, 700));
            content.UpdateLayout();
            Console.WriteLine("Probe: layout arranged.");
            typeof(MainWindow).GetMethod("ApplyLiquidGlassSkin", Private)!.Invoke(window, null);
            Console.WriteLine("Probe: glass skin applied.");
            var glass = (LiquidGlassController)typeof(MainWindow).GetField("_liquidGlass", Private)!.GetValue(window)!;
            var frame = new DispatcherFrame();
            long deadline = Environment.TickCount64 + 10000;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                glass = (LiquidGlassController)typeof(MainWindow).GetField("_liquidGlass", Private)!.GetValue(window)!;
                if (glass.HasPresentedFrame || Environment.TickCount64 >= deadline)
                { timer.Stop(); frame.Continue = false; }
            };
            timer.Start(); Dispatcher.PushFrame(frame);
            object? Read(object target, string name) => target.GetType().GetField(name, Private)?.GetValue(target);
            Console.WriteLine($"Main: visible={glass.HasPresentedFrame} region={typeof(MainWindow).GetMethod("GetGlassCaptureRegion", Private)!.Invoke(window, null)} native={Read(glass, "_gpuMode")} generation={Read(glass, "_renderGeneration")}");
            var presenter = Read(glass, "_d3dPresenter");
            if (presenter != null)
            {
                Console.WriteLine(presenter.GetType().GetProperty("CaptureStatus", Private)!.GetValue(presenter));
                foreach (string name in new[] { "_acceptingFrames", "_sessionGeneration", "_pendingFrame", "_gpuHandle", "_frameWidth", "_frameHeight" })
                    Console.WriteLine($"{name}={Read(presenter, name)}");
            }
            if (!glass.HasPresentedFrame) throw new TimeoutException("Production MainWindow did not present a frame.");
            double sigma = (double)Read(glass, "_blurSigma")!;
            double dpiScale = (double)typeof(MainWindow).GetMethod("GetGlassDpiScale", Private)!.Invoke(window, null)!;
            if (Math.Abs(sigma - 28 * .05 * .05 * dpiScale) > 1e-7)
                throw new InvalidOperationException($"5% blur lost its fractional strength: {sigma} at DPI {dpiScale}");
            Console.WriteLine($"PASS: production 5% blur retains sigma={sigma:F3} at DPI scale {dpiScale:F2}.");
            var combined = new List<System.Windows.Media.CombinedGeometry>();
            void FindClips(System.Windows.DependencyObject visual)
            {
                if (visual is System.Windows.Media.Visual v && System.Windows.Media.VisualTreeHelper.GetClip(v) is System.Windows.Media.CombinedGeometry clip)
                {
                    combined.Add(clip);
                    Console.WriteLine($"Combined clip: {visual.GetType().Name} {(visual as FrameworkElement)?.Name}");
                }
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual); ++i)
                    FindClips(System.Windows.Media.VisualTreeHelper.GetChild(visual, i));
            }
            FindClips(window);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; ++i) foreach (var clip in combined) _ = clip.Bounds;
            Console.WriteLine($"Clip bounds x100: {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024.0:F1} KiB allocated, {combined.Count} combined clips");
            typeof(MainWindow).GetMethod("OnLiquidGlassFrameUpdate", Private)!.Invoke(window, [null, EventArgs.Empty]);
            var backdrop = (FrameworkElement)window.FindName("GlassBackdropHost");
            var image = (System.Windows.Controls.Image)window.FindName("GlassBackdropImage");
            if (backdrop.Opacity <= 0 || backdrop.Visibility != Visibility.Visible || image.Source == null || glass.ImageSource is not D3DImage)
                throw new InvalidOperationException("The native frame is not exposed by the glass material.");
            Console.WriteLine("PASS: production MainWindow receives native glass with lazy resources and its real region provider.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FAIL: {ex}"); return 1; }
        finally
        {
            window.Close();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
