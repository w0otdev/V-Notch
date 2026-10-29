using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services.Spotlight;

namespace VNotch.Services;

internal static partial class ServicePrewarmer
{
    private const string NativeDll = "VNotch.Native.dll";
    private const string LogCategory = "PREWARM";

    [LibraryImport(NativeDll, EntryPoint = "vn_service_prewarmer_get_initial_delay_ms")]
    private static partial int NativeGetInitialDelayMs(out int outDelayMs);

    [LibraryImport(NativeDll, EntryPoint = "vn_service_prewarmer_should_warmup_spotlight")]
    private static partial int NativeShouldWarmupSpotlight(
        byte hasSettings,
        byte enableSpotlight,
        out byte outShouldWarmup);

    public static Task Prewarm(IServiceProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        return Task.Run(async () =>
        {
            try
            {
                // Give MainWindow time to finish its first frame layout and render before background warmups
                NativeGetInitialDelayMs(out int delayMs);
                await Task.Delay(delayMs).ConfigureAwait(false);
                var currentThread = System.Threading.Thread.CurrentThread;
                var previousPriority = currentThread.Priority;
                try
                {
                    currentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
                    RunBackgroundWarmups(provider);
                }
                finally
                {
                    currentThread.Priority = previousPriority;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(LogCategory, ex, "Background warmup loop failed");
            }
        });
    }

    private static void RunBackgroundWarmups(IServiceProvider provider)
    {
        var settings = WarmupSettings(provider);
        Task spotlightTask = WarmupSpotlight(provider, settings);
        WarmupBattery(provider);
        WarmupVolume(provider);
        WarmupWindowTitleScanner(provider);

        RuntimeLog.Log(LogCategory, "background warmup complete");
        SmartThumbnailCropService.TryMinimizeNativeHeap(trimWorkingSet: true);
        if (!spotlightTask.IsCompleted)
        {
            _ = spotlightTask.ContinueWith(
                static _ => SmartThumbnailCropService.TryMinimizeNativeHeap(trimWorkingSet: true),
                TaskScheduler.Default);
        }
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

    private static Task WarmupSpotlight(IServiceProvider provider, NotchSettings? settings)
    {
        // Skip Start Menu and apps indexing if Spotlight is disabled to avoid
        // unnecessary launch overhead.
        NativeShouldWarmupSpotlight(
            settings != null ? (byte)1 : (byte)0,
            settings?.EnableSpotlight == true ? (byte)1 : (byte)0,
            out byte shouldWarmup);
        if (shouldWarmup != 0)
        {
            try
            {
                return provider.GetService<SpotlightSearchService>()?.WarmupAsync() ?? Task.CompletedTask;
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
        return Task.CompletedTask;
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
}
