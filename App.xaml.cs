using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;

namespace VNotch;

public partial class App : Application
{
    private static SingleInstanceGuard? _guard;
    private static int _fatalUiExceptionInProgress;
    private const string MutexName = "VNotch_SingleInstance_Mutex";

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var setupSource = TryGetArgumentValue(e.Args, "--setup-source");
        var exeName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        var launchSetup = e.Args.Contains("--setup") || !string.IsNullOrWhiteSpace(setupSource) || exeName.Contains("Setup", StringComparison.OrdinalIgnoreCase);
        if (launchSetup)
        {
            var setupWindow = new SetupWindow(setupSource);
            setupWindow.ShowDialog();
            Shutdown(setupWindow.ResultExitCode);
            return;
        }

        var earlySettings = new SettingsService();
        var loadedSettings = earlySettings.Load();
        Loc.SetLanguage(loadedSettings.Language);
        AnimationConfig.Configure(loadedSettings.AnimationFps);
        ApplyProcessPriority(loadedSettings.ProcessPriority);

        if (e.Args.Contains("--uninstall"))
        {
            SetupOperations.RunUninstallFlow();
            return;
        }

        _guard = new SingleInstanceGuard(MutexName);
        bool ownsMutex = _guard.TryAcquire();

        if (!ownsMutex && e.Args.Contains("--restart"))
        {
            ownsMutex = _guard.TryWaitForPreviousInstance(TimeSpan.FromSeconds(10));
        }

        if (!ownsMutex)
        {
            MessageBox.Show(Loc.Get("error.alreadyRunning"), "V-Notch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        RuntimeLog.InitializeNewSession("vnotch-debug.log");
        RuntimeLog.Log("SYSTEM", $"Application startup. Log file: {RuntimeLog.LogPath}");

        DispatcherUnhandledException += (s, args) =>
        {
            // MessageBox.Show runs a nested dispatcher loop. Without this guard,
            // another queued UI callback can fail while the first fatal dialog is
            // open and recursively create an entire stack of error dialogs.
            if (Volatile.Read(ref _fatalUiExceptionInProgress) != 0)
            {
                args.Handled = true;
                return;
            }

            if (IsRecoverableException(args.Exception))
            {
                RuntimeLog.Error("UNHANDLED-UI-RECOVERED", args.Exception,
                    "Recovered a known animation or media exception on the UI dispatcher");
                args.Handled = true;
                return;
            }

            if (Interlocked.Exchange(ref _fatalUiExceptionInProgress, 1) != 0)
            {
                args.Handled = true;
                return;
            }

            args.Handled = true;
            RuntimeLog.Error("UNHANDLED-UI-FATAL", args.Exception,
                "Unexpected UI dispatcher exception; shutting down to avoid continuing in an unknown state");
            try { RuntimeLog.FlushAsync().Wait(TimeSpan.FromSeconds(1)); }
            catch { }

            try
            {
                MessageBox.Show(
                    Loc.Get("app.fatalClose", RuntimeLog.LogPath),
                    Loc.Get("error.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Shutdown(1);
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                RuntimeLog.Error("UNHANDLED-BG", ex, "Background thread crash");

                // The process may terminate immediately after this callback, so
                // synchronously preserve only this exceptional final batch.
                if (args.IsTerminating)
                {
                    try { RuntimeLog.FlushAsync().Wait(TimeSpan.FromSeconds(1)); }
                    catch { }
                }
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            RuntimeLog.Error("UNOBSERVED-TASK", args.Exception?.InnerException ?? args.Exception!,
                "Unobserved task exception");
            args.SetObserved();
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        ServicePrewarmer.Prewarm(Services);

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        CheckAndShowPostUpdateReleasePage(loadedSettings, earlySettings);

        base.OnStartup(e);
    }

    private void ConfigureServices(IServiceCollection services)
    {

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IMediaMetadataLookupService, MediaMetadataLookupService>();
        services.AddSingleton<IMediaArtworkService, MediaArtworkService>();
        services.AddSingleton<IColorExtractionService, ColorExtractionService>();
        services.AddSingleton<IWindowTitleScanner, WindowTitleScanner>();
        services.AddSingleton<IMediaDetectionService, MediaDetectionService>();
        services.AddSingleton<IVolumeService, VolumeService>();
        services.AddSingleton<AudioMixerService>();
        services.AddSingleton<IBatteryService, BatteryServiceImpl>();
        services.AddSingleton<BluetoothMonitorService>();
        services.AddSingleton<PrivacyIndicatorService>();
        services.AddSingleton<IDispatcherService>(sp =>
            new DispatcherService(Current.Dispatcher));
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IWeatherService, WeatherService>();
        services.AddSingleton<ISpotlightProvider, AppSearchProvider>();
        services.AddSingleton<ISpotlightProvider, SystemFileSearchProvider>();
        services.AddSingleton<ISpotlightProvider, EverythingSearchProvider>();
        services.AddSingleton<ISpotlightProvider, WindowsSearchProvider>();
        services.AddSingleton<ISpotlightProvider, CalculatorProvider>();
        services.AddSingleton<SpotlightUsageStore>();
        services.AddSingleton<SpotlightSearchService>();
        services.AddSingleton<SpotlightLauncher>();
        services.AddSingleton<SpotlightViewModel>();
        services.AddSingleton(sp => new SpotlightWindow(
            sp.GetRequiredService<SpotlightViewModel>(),
            sp.GetRequiredService<SpotlightLauncher>()));
        services.AddSingleton<Controllers.ISpotlightController>(sp =>
            new Controllers.SpotlightController(() => sp.GetRequiredService<SpotlightWindow>()));
        // This is the application state owner used by both the running window and unit tests.
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<BatteryModule>();
        services.AddSingleton<CalendarModule>();
        services.AddSingleton<BluetoothModule>();
        services.AddSingleton<PrivacyIndicatorModule>();
        services.AddSingleton<WeatherModule>();
        services.AddSingleton<SystemMonitorModule>();
        services.AddSingleton<IModuleLifecycleManager>(sp =>
        {
            var host = new ModuleLifecycleManager();
            host.Register(sp.GetRequiredService<BatteryModule>());
            host.Register(sp.GetRequiredService<CalendarModule>());
            host.Register(sp.GetRequiredService<BluetoothModule>());
            host.Register(sp.GetRequiredService<PrivacyIndicatorModule>());
            host.Register(sp.GetRequiredService<WeatherModule>());
            host.Register(sp.GetRequiredService<SystemMonitorModule>());
            return host;
        });

        services.AddSingleton<MainWindow>();
    }

    private static string FormatVersion(Version v)
    {
        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{v.Build}";
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _guard?.Dispose();
            RuntimeLog.Log("SYSTEM", "Application exit");
            base.OnExit(e);
        }
        finally
        {
            RuntimeLog.Shutdown(TimeSpan.FromSeconds(2));
        }
    }
    private static bool IsRecoverableException(Exception ex)
    {
        return ex is RecoverableAnimationException
            or RecoverableMediaException
            or System.Runtime.InteropServices.COMException;
    }

    private static void CheckAndShowPostUpdateReleasePage(VNotch.Models.NotchSettings settings, SettingsService settingsService)
    {
        try
        {
            var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion == null) return;

            var currentVersionStr = FormatVersion(currentVersion);
            bool needSave = false;

            if (settings.LastRunVersion != currentVersionStr)
            {
                settings.LastRunVersion = currentVersionStr;
                needSave = true;
            }

            if (!settings.HasSeenLiquidGlassIntro)
            {
                settings.HasSeenLiquidGlassIntro = true;
                needSave = true;

                if (needSave)
                {
                    settingsService.Save(settings);
                    needSave = false;
                }

                Current.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    try
                    {
                        var introWindow = new IntroducingWindow();
                        introWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        introWindow.ShowDialog();
                    }
                    catch (System.Exception introEx)
                    {
                        RuntimeLog.Error("INTRO-WINDOW", introEx, "Failed to show introducing window");
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            if (needSave)
            {
                settingsService.Save(settings);
            }
        }
        catch (System.Exception ex)
        {
            RuntimeLog.Error("POST-UPDATE", ex, "Failed to check/show post-update release page");
        }
    }

    private static string? TryGetArgumentValue(string[] args, string argumentName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, argumentName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                return null;
            }

            var prefix = argumentName + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private void ApplyProcessPriority(string priority)
    {
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            p.PriorityClass = priority switch
            {
                "High" => System.Diagnostics.ProcessPriorityClass.High,
                "RealTime" => System.Diagnostics.ProcessPriorityClass.RealTime,
                _ => System.Diagnostics.ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("SYSTEM", $"Failed to set process priority to {priority}: {ex.Message}");
        }
    }
}
