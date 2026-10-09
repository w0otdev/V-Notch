using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Modules;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using VNotch.Windows;

namespace VNotch.Services;

/// <summary>Registers the application composition without constructing a WPF Application.</summary>
public static class ServiceConfigurator
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IStartupManager, WindowsStartupManager>();
        services.AddSingleton<ISettingsApplicationService, SettingsApplicationService>();
        services.AddSingleton<IMediaMetadataLookupService, MediaMetadataLookupService>();
        services.AddSingleton<IMediaArtworkService, MediaArtworkService>();
        services.AddSingleton<IColorExtractionService, ColorExtractionService>();
        services.AddSingleton<IWindowTitleScanner, WindowTitleScanner>();
        services.AddSingleton<IMediaDetectionService, MediaDetectionService>();
        services.AddSingleton<IVolumeService, VolumeService>();
        services.AddSingleton<AudioMixerService>();
        services.AddSingleton<IBatteryService, BatteryServiceImpl>();
        services.AddSingleton(sp => new BluetoothMonitorService(Application.Current.Dispatcher));
        services.AddSingleton<PrivacyIndicatorService>();
        services.AddSingleton<IDispatcherService>(sp =>
            new DispatcherService(Application.Current.Dispatcher));
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<AppFileCleanupService>();
        services.AddSingleton<IWeatherService, WeatherService>();
        services.AddSingleton<ISpotlightProvider, AppSearchProvider>();
        services.AddSingleton<ISpotlightProvider, SystemCommandProvider>();
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
        services.AddSingleton<Controllers.NotchTransitionCoordinator>();
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
}
