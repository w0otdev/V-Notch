using System.Diagnostics;
using System.IO;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightLauncher
{
    public bool TryLaunch(SpotlightSearchItem item)
    {
        if (!IsValidTarget(item)) return false;

        try
        {
            return Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true }) != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to open {item.Kind}: {item.Target}");
            return false;
        }
    }

    public bool TryRevealInExplorer(SpotlightSearchItem item)
    {
        if (!CanReveal(item)) return false;

        try
        {
            return Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Target}\"")
            {
                UseShellExecute = true
            }) != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to reveal {item.Kind}: {item.Target}");
            return false;
        }
    }

    internal static bool CanReveal(SpotlightSearchItem item) =>
        item.Kind is SpotlightResultKind.File or SpotlightResultKind.Folder && IsValidTarget(item);

    internal static bool IsValidTarget(SpotlightSearchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Target)
            || item.Target.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            return false;
        }

        return item.Kind switch
        {
            SpotlightResultKind.Application =>
                item.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                    && item.Target.Length > "shell:AppsFolder\\".Length
                || File.Exists(item.Target)
                || Directory.Exists(item.Target),
            SpotlightResultKind.File => File.Exists(item.Target),
            SpotlightResultKind.Folder => Directory.Exists(item.Target),
            _ => false
        };
    }
}
