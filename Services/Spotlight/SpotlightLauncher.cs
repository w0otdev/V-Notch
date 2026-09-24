using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightLauncher
{
    public bool TryLaunch(SpotlightSearchItem item, bool systemActionConfirmed = false)
    {
        if (!IsValidTarget(item)) return false;
        if (SpotlightSystemCatalog.RequiresConfirmation(item) && !systemActionConfirmed) return false;

        try
        {
            if (item.Kind == SpotlightResultKind.Settings)
                return global::Windows.System.Launcher.LaunchUriAsync(new Uri(item.Target))
                    .AsTask().GetAwaiter().GetResult();
            if (item.Kind == SpotlightResultKind.SystemAction)
                return TryRunSystemAction(item.Target);
            return Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true }) != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to open {item.Kind}: {item.Target}");
            return false;
        }
    }

    private static bool TryRunSystemAction(string target)
    {
        if (target == "vnotch-action:lock") return LockWorkStation();
        if (target == "shell:RecycleBinFolder")
        {
            using var explorer = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = true,
                ArgumentList = { "shell:RecycleBinFolder" }
            });
            return explorer != null;
        }

        string[]? arguments = target switch
        {
            "vnotch-action:restart" => ["/r", "/t", "0"],
            "vnotch-action:shutdown" => ["/s", "/t", "0"],
            "vnotch-action:signOut" => ["/l"],
            "vnotch-action:hibernate" => ["/h"],
            "vnotch-action:advancedRestart" => ["/r", "/o", "/t", "0"],
            _ => null
        };
        if (arguments == null) return false;

        // Never force-close applications. A nonzero timeout would implicitly
        // enable /f, so keep /t 0 and let Windows handle unsaved-work blockers.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process == null) return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public bool TryRevealInExplorer(SpotlightSearchItem item)
    {
        if (!CanReveal(item)) return false;

        try
        {
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string safeTarget = item.Target.Replace("\"", "");
            return Process.Start(new ProcessStartInfo(explorerPath, $"/select,\"{safeTarget}\"")
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
            SpotlightResultKind.Settings or SpotlightResultKind.SystemAction =>
                SpotlightSystemCatalog.IsKnownTarget(item),
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
