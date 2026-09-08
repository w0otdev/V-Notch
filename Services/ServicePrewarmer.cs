using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services.Spotlight;

namespace VNotch.Services;

internal static class ServicePrewarmer
{
    private const string LogCategory = "PREWARM";

    public static void Prewarm(IServiceProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        try
        {
            ResolveAll(provider);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Service resolution failed");
        }

        Task.Run(async () =>
        {
            try
            {
                // Give MainWindow time to finish its first frame layout and render before background warmups
                await Task.Delay(1200).ConfigureAwait(false);
                RunBackgroundWarmups(provider);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(LogCategory, ex, "Background warmup loop failed");
            }
        });
    }

    private static void ResolveAll(IServiceProvider provider)
    {
        SafeResolve<ISettingsService>(provider);
        SafeResolve<IDispatcherService>(provider);
        SafeResolve<IMediaMetadataLookupService>(provider);
        SafeResolve<IMediaArtworkService>(provider);
        SafeResolve<IColorExtractionService>(provider);
        SafeResolve<IWindowTitleScanner>(provider);
        SafeResolve<IMediaDetectionService>(provider);
        SafeResolve<IVolumeService>(provider);
        SafeResolve<IBatteryService>(provider);
        SafeResolve<IUpdateService>(provider);
        SafeResolve<IWeatherService>(provider);
        SafeResolve<SpotlightSearchService>(provider);

        SafeResolve<BluetoothMonitorService>(provider);
        SafeResolve<PrivacyIndicatorService>(provider);
        SafeResolve<AudioMixerService>(provider);

        SafeResolve<BatteryModule>(provider);
        SafeResolve<CalendarModule>(provider);
        SafeResolve<BluetoothModule>(provider);
        SafeResolve<PrivacyIndicatorModule>(provider);
        SafeResolve<WeatherModule>(provider);
        SafeResolve<SystemMonitorModule>(provider);
        SafeResolve<IModuleLifecycleManager>(provider);
    }

    private static void RunBackgroundWarmups(IServiceProvider provider)
    {
        var settings = WarmupSettings(provider);
        WarmupSpotlight(provider, settings);
        WarmupBattery(provider);
        WarmupVolume(provider);
        WarmupWindowTitleScanner(provider);
        WarmupBluetooth(provider);
        WarmupAudioMixer(provider);
        WarmupPrivacyIndicator(provider);

        RuntimeLog.Log(LogCategory, "background warmup complete");
    }

    private static NotchSettings? WarmupSettings(IServiceProvider provider)
    {
        try
        {
            var settings = provider.GetService<ISettingsService>()?.Load();
            if (settings != null)
            {
                RuntimeLog.Log(LogCategory, $"settings loaded (lang={settings.Language})");

                if (settings.EnableSmartCrop)
                {
                    var artwork = provider.GetService<IMediaArtworkService>();
                    artwork?.ConfigureSmartCrop(true);
                }
            }
            return settings;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Settings warmup failed");
            return null;
        }
    }

    private static void WarmupSpotlight(IServiceProvider provider, NotchSettings? settings)
    {
        // Building the app index walks both Start Menu trees and shell:AppsFolder.
        // Skip it entirely when Spotlight is switched off, otherwise every launch
        // pays for a feature the user cannot reach.
        if (settings?.EnableSpotlight ?? true)
        {
            try
            {
                _ = provider.GetService<SpotlightSearchService>()?.WarmupAsync();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(LogCategory, ex, "Spotlight app index warmup failed");
            }
        }
        else
        {
            RuntimeLog.Log(LogCategory, "Spotlight disabled; app index warmup skipped");
        }
    }

    private static void WarmupBattery(IServiceProvider provider)
    {
        try
        {
            var battery = provider.GetService<IBatteryService>();
            _ = battery?.GetBatteryInfo();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Battery warmup failed");
        }
    }

    private static void WarmupVolume(IServiceProvider provider)
    {
        try
        {
            var volume = provider.GetService<IVolumeService>();
            if (volume != null && volume.IsAvailable)
            {
                _ = volume.GetVolume();
                _ = volume.GetMute();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Volume warmup failed");
        }
    }

    private static void WarmupWindowTitleScanner(IServiceProvider provider)
    {
        try
        {
            var scanner = provider.GetService<IWindowTitleScanner>();
            _ = scanner?.GetAllWindowTitles(isThrottled: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "WindowTitleScanner warmup failed");
        }
    }

    private static void WarmupBluetooth(IServiceProvider provider)
    {
        try
        {
            provider.GetService<BluetoothMonitorService>()?.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Bluetooth watcher warmup failed");
        }
    }

    private static void WarmupAudioMixer(IServiceProvider provider)
    {
        try
        {
            var mixer = provider.GetService<AudioMixerService>();
            if (mixer != null)
            {
                // Prewarm NAudio enumerator and COM endpoints
                _ = mixer.GetOutputDevices();
                _ = mixer.GetSessions(includeIcons: false);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Audio mixer warmup failed");
        }
    }

    private static void WarmupPrivacyIndicator(IServiceProvider provider)
    {
        try
        {
            provider.GetService<PrivacyIndicatorService>()?.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Privacy indicator warmup failed");
        }
    }

    private static void SafeResolve<T>(IServiceProvider provider) where T : class
    {
        try
        {
            _ = provider.GetService<T>();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, $"Resolve<{typeof(T).Name}> failed");
        }
    }
}
