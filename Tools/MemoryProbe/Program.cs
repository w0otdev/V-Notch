using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Services.Clipboard;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class ProbeApplication : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/memory-probe");
        int duration = args.Length > 1 ? int.Parse(args[1]) : 120;
        if (duration < 30 || duration > 3600) throw new ArgumentOutOfRangeException(nameof(duration), "Use 30-3600 seconds.");
        bool exercise = args.Contains("--exercise");
        bool fullServices = args.Contains("--full-services");
        Directory.CreateDirectory(root);
        var app = new ProbeApplication();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/V-Notch;component/Controls/AccentThemeResources.xaml", UriKind.Relative)
        });
        app.Resources["SFProDisplay"] = AppFonts.Get("Segoe UI Emoji, Segoe UI, Arial");
        app.Resources["SFProText"] = AppFonts.Get("Segoe UI Emoji, Segoe UI, Arial");
        app.Resources["IconFont"] = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync(root, duration, exercise, fullServices);
        task.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => app.Shutdown()), TaskScheduler.Default);
        app.Run();
        return task.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string root, int duration, bool exercise, bool fullServices)
    {
        using var original = new SettingsService();
        var snapshot = original.Load();
        if (fullServices)
        {
            snapshot.EnableWeather = true;
            snapshot.EnableSpotlight = true;
            snapshot.EnablePrivacyIndicators = true;
            snapshot.EnableAiUsageMonitoring = true;
            snapshot.EnableSmartCrop = true;
            snapshot.EnableSubjectBlur = true;
            snapshot.EnableLiveTranslation = true;
            // Register translation, but do not capture text from other apps.
            snapshot.AutoLiveTranslation = false;
        }
        // Preserve service/visual settings. Only persist into this probe's directory.
        using var settings = (SettingsService)typeof(SettingsService).GetConstructor(Private, null,
            [typeof(string), typeof(Action<string>)], null)!.Invoke(
            [Path.Combine(root, "settings.json"), (Action<string>)(_ => { })]);
        settings.Save(snapshot);
        var services = new ServiceCollection();
        ServiceConfigurator.ConfigureServices(services);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton(new ClipboardHistoryStore(Path.Combine(root, "Clipboard")));
        await using var provider = services.BuildServiceProvider();
        typeof(App).GetMethod("SetServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [provider]);
        InvokeStatic("VNotch.Services.NetworkPrivacy", "get_Current")!.GetType()
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(InvokeStatic("VNotch.Services.NetworkPrivacy", "get_Current"), [snapshot]);
        InvokeStatic("VNotch.Services.AnimationConfig", "Configure", snapshot.AnimationFps, snapshot.AutoAnimationFps);
        Loc.SetLanguage(snapshot.Language);
        InvokeStatic("VNotch.Services.AccentTheme", "Apply", snapshot.AccentColor, false);
        var window = provider.GetRequiredService<MainWindow>();
        window.ShowActivated = false;
        using var process = Process.GetCurrentProcess();
        var samples = new List<object>();
        var watch = Stopwatch.StartNew();
        string? failure = null;
        try
        {
            window.Show();
            await (Task)InvokeStatic("VNotch.Services.ServicePrewarmer", "Prewarm", provider)!;
            var backgroundModules = new NotchModuleBase[] {
                provider.GetRequiredService<BatteryModule>(), provider.GetRequiredService<BluetoothModule>(),
                provider.GetRequiredService<WeatherModule>(), provider.GetRequiredService<PrivacyIndicatorModule>()
            };
            for (int second = 0; second < duration; second++)
            {
                if (exercise && second is 40 or 55 or 70 or 85)
                {
                    var coordinator = (NotchTransitionCoordinator)typeof(MainWindow)
                        .GetField("_transitionCoordinator", Private)!.GetValue(window)!;
                    coordinator.RequestView(second is 40 or 70 ? NotchView.Media : NotchView.Compact, "memory-probe");
                }
                var media = typeof(MainWindow).GetField("_currentMediaInfo", Private)!.GetValue(window) as MediaInfo;
                if (fullServices && second >= 30 && backgroundModules.FirstOrDefault(module => !module.IsRunning) is { } stopped)
                    throw new InvalidOperationException($"{stopped.ModuleName} stopped during full-service measurement.");
                samples.Add(new
                {
                    Memory = ProcessMemory.Read(process, watch.Elapsed.TotalSeconds, second < 30 ? "warmup" : "steady"),
                    MediaPlaying = media?.IsPlaying == true,
                    SettingsPrepared = typeof(MainWindow).GetField("_preparedSettingsWindow", Private)!.GetValue(window) != null,
                    Expanded = (bool)typeof(MainWindow).GetProperty("_isExpanded", Private)!.GetValue(window)!,
                    CoreServicesStarted = (bool)typeof(MainWindow).GetField("_coreModulesStarted", Private)!.GetValue(window)!
                });
                await Task.Delay(1000);
            }
            if (!(bool)typeof(MainWindow).GetField("_coreModulesStarted", Private)!.GetValue(window)!)
                throw new InvalidOperationException("Core services did not start; this is not a valid app measurement.");
        }
        catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex); }
        finally
        {
            File.WriteAllText(Path.Combine(root, "memory.json"), JsonSerializer.Serialize(new
            {
                ProcessId = process.Id, TimestampUtc = DateTime.UtcNow,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                GcConfiguration = GC.GetConfigurationVariables(),
                Scope = "Release production MainWindow and DI services, isolated settings/empty clipboard; no AI generation, camera capture or file-index workload",
                Settings = new { snapshot.NotchStyle, snapshot.EnableSmartCrop, snapshot.EnableSubjectBlur,
                    snapshot.EnableWeather, snapshot.EnableSpotlight, snapshot.EnablePrivacyIndicators,
                    snapshot.EnableAiUsageMonitoring, snapshot.EnableLiveTranslation,
                    snapshot.AnimationFps, snapshot.AutoAnimationFps },
                Exercise = exercise, FullServices = fullServices, Failure = failure, Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
        Console.WriteLine(Path.Combine(root, "memory.json"));
        return failure == null ? 0 : 1;
    }

    private static object? InvokeStatic(string type, string method, params object?[] args) =>
        typeof(App).Assembly.GetType(type)!.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args);
}
