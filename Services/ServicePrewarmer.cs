using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;

namespace VNotch.Services;

internal static class ServicePrewarmer
{
    private const string LogCategory = "PREWARM";

    public static Task Prewarm(IServiceProvider provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        return Task.Run(async () =>
        {
            try
            {
                // Give MainWindow time to finish its first frame layout and render before background warmups
                await Task.Delay(1200).ConfigureAwait(false);
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
        // Build file indexes on the first actual Spotlight query. They are not
        // needed for startup and can enumerate thousands of system files.
        WarmupBattery(provider);
        WarmupVolume(provider);
        WarmupWindowTitleScanner(provider);

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
