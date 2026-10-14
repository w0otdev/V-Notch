using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Controllers;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed class SpotlightLauncher
{
    public async Task<bool> TryLaunchAsync(SpotlightSearchItem item, bool systemActionConfirmed = false)
    {
        if (!IsValidTarget(item)) return false;
        if (SpotlightSystemCatalog.RequiresConfirmation(item) && !systemActionConfirmed) return false;

        try
        {
            if (item.Kind == SpotlightResultKind.Settings)
                return await global::Windows.System.Launcher.LaunchUriAsync(new Uri(item.Target))
                    .AsTask().ConfigureAwait(false);
            if (item.Kind == SpotlightResultKind.SystemAction)
                return await TryRunSystemActionAsync(item.Target).ConfigureAwait(false);
            return Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true }) != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to open {item.Kind}: {item.Target}");
            return false;
        }
    }

    private static async Task<bool> TryRunSystemActionAsync(string target)
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
        int waitMs = target == "vnotch-action:hibernate" ? 250 : 3000;
        using var cts = new CancellationTokenSource(waitMs);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            // shutdown.exe /h blocks until the machine wakes from hibernation;
            // if it has not exited with an error within 250ms, hibernation is proceeding.
            return !process.HasExited || process.ExitCode == 0;
        }
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
            var startInfo = new ProcessStartInfo(explorerPath)
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add($"/select,{item.Target}");
            return Process.Start(startInfo) != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to reveal {item.Kind}: {item.Target}");
            return false;
        }
    }

    public bool TryLaunchElevated(SpotlightSearchItem item) =>
        TryLaunchElevated(item, out _);

    public bool TryLaunchElevated(SpotlightSearchItem item, out bool cancelled)
    {
        cancelled = false;
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
            // The user declined the UAC prompt; keep Spotlight open without recording usage or showing an error.
            cancelled = true;
            return false;
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
            || item.Target.IndexOfAny(['\0', '\r', '\n']) >= 0
            || LocalPathPolicy.IsUncPath(item.Target))
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
