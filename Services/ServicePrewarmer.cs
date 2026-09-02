using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services.Spotlight;

namespace VNotch.Services;

internal static class ServicePrewarmer
{
    public static void Prewarm(IServiceProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        try
        {
            ResolveAll(provider);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Service resolution failed");
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
                RuntimeLog.Error("PREWARM", ex, "Background warmup loop failed");
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
        NotchSettings? settings = null;

        try
        {
            settings = provider.GetService<ISettingsService>()?.Load();
            if (settings != null)
            {
                RuntimeLog.Log("PREWARM", $"settings loaded (lang={settings.Language})");

                if (settings.EnableSmartCrop)
                {
                    var artwork = provider.GetService<IMediaArtworkService>();
                    artwork?.ConfigureSmartCrop(true);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Settings warmup failed");
        }

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
                RuntimeLog.Error("PREWARM", ex, "Spotlight app index warmup failed");
            }
        }
        else
        {
            RuntimeLog.Log("PREWARM", "Spotlight disabled; app index warmup skipped");
        }

        try
        {
            var battery = provider.GetService<IBatteryService>();
            _ = battery?.GetBatteryInfo();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Battery warmup failed");
        }

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
            RuntimeLog.Error("PREWARM", ex, "Volume warmup failed");
        }

        try
        {
            var scanner = provider.GetService<IWindowTitleScanner>();
            _ = scanner?.GetAllWindowTitles(isThrottled: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "WindowTitleScanner warmup failed");
        }

        try
        {
            provider.GetService<BluetoothMonitorService>()?.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Bluetooth watcher warmup failed");
        }

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
            RuntimeLog.Error("PREWARM", ex, "Audio mixer warmup failed");
        }

        try
        {
            provider.GetService<PrivacyIndicatorService>()?.Start();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, "Privacy indicator warmup failed");
        }

        RuntimeLog.Log("PREWARM", "background warmup complete");
        MemoryOptimizerService.Instance.SchedulePostStartupTrim(1800, 4500);
    }

    private static T? SafeResolve<T>(IServiceProvider provider) where T : class
    {
        try
        {
            return provider.GetService<T>();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PREWARM", ex, $"Resolve<{typeof(T).Name}> failed");
            return null;
        }
    }
}
