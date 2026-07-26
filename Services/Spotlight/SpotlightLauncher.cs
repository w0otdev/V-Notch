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

    public bool TryLaunchElevated(SpotlightSearchItem item)
    {
        if (!CanLaunchElevated(item)) return false;

        try
        {
            return Process.Start(new ProcessStartInfo(item.Target)
            {
                UseShellExecute = true,
                Verb = "runas"
            }) != null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user declined the UAC prompt; that is a choice, not a failure.
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to elevate {item.Kind}: {item.Target}");
            return false;
        }
    }

    internal static bool CanReveal(SpotlightSearchItem item) =>
        IsValidTarget(item) && item.Kind switch
        {
            SpotlightResultKind.File or SpotlightResultKind.Folder => true,
            // Shortcuts and exes can be revealed; shell:AppsFolder targets cannot.
            SpotlightResultKind.Application => File.Exists(item.Target),
            _ => false
        };

    internal static bool CanLaunchElevated(SpotlightSearchItem item) =>
        item.Kind is SpotlightResultKind.Application or SpotlightResultKind.File
        && IsValidTarget(item)
        && File.Exists(item.Target);

    /// <summary>
    /// The text put on the clipboard for "copy path"; the computed value for
    /// calculations, the target path for everything file-backed.
    /// </summary>
    internal static string? GetCopyableText(SpotlightSearchItem item)
    {
        if (item.Kind == SpotlightResultKind.Calculation)
            return string.IsNullOrWhiteSpace(item.Target) ? null : item.Target;
        return CanReveal(item) ? item.Target : null;
    }

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
            // Calculations are copied by the window, never process-launched.
            _ => false
        };
    }
}
