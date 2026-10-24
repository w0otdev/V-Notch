using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VNotch.Controllers;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal sealed partial class SpotlightLauncher
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_launcher_classify_system_action(
        ReadOnlySpan<char> target,
        int targetLen,
        out int outAction,
        out int outWaitMs);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_launcher_is_target_string_safe(
        ReadOnlySpan<char> target,
        int targetLen,
        out byte outSafe);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_launcher_is_apps_folder_target(
        ReadOnlySpan<char> target,
        int targetLen,
        out byte outIsAppsFolder);
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
            using var process = Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-LAUNCH", ex, $"Failed to open {item.Kind}: {item.Target}");
            return false;
        }
    }

    private static async Task<bool> TryRunSystemActionAsync(string target)
    {
        _ = vn_spotlight_launcher_classify_system_action(target.AsSpan(), target.Length, out int actionCode, out int waitMs);
        if (actionCode == 1) return LockWorkStation();
        if (actionCode == 2)
        {
            using var explorer = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = true,
                ArgumentList = { "shell:RecycleBinFolder" }
            });
            return true;
        }

        string[]? arguments = actionCode switch
        {
            3 => ["/r", "/t", "0"],
            4 => ["/s", "/t", "0"],
            5 => ["/l"],
            6 => ["/h"],
            7 => ["/r", "/o", "/t", "0"],
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

    private static bool LockWorkStation() => Win32Interop.LockWorkStation();

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
            using var process = Process.Start(startInfo);
            return process != null;
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
            using var process = Process.Start(new ProcessStartInfo(item.Target)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
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
        if (item.Target == null
            || vn_spotlight_launcher_is_target_string_safe(item.Target.AsSpan(), item.Target.Length, out byte isSafe) != 0
            || isSafe == 0
            || LocalPathPolicy.IsUncPath(item.Target))
        {
            return false;
        }

        return item.Kind switch
        {
            SpotlightResultKind.Settings or SpotlightResultKind.SystemAction =>
                SpotlightSystemCatalog.IsKnownTarget(item),
            SpotlightResultKind.Application =>
                (vn_spotlight_launcher_is_apps_folder_target(item.Target.AsSpan(), item.Target.Length, out byte isApps) == 0 && isApps != 0)
                || File.Exists(item.Target)
                || Directory.Exists(item.Target),
            SpotlightResultKind.File => File.Exists(item.Target),
            SpotlightResultKind.Folder => Directory.Exists(item.Target),
            // Calculations are copied by the window, never process-launched.
            _ => false
        };
    }
}
