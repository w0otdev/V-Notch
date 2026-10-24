using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

internal static partial class InstallDirectoryCleanup
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_install_cleanup_has_valid_input_syntax(
        ReadOnlySpan<char> dirPtr,
        int dirLen,
        out byte outValid);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_install_cleanup_has_valid_full_path_segments(
        ReadOnlySpan<char> fullPtr,
        int fullLen,
        ReadOnlySpan<char> rootPtr,
        int rootLen,
        out byte outValid);

    // Paths are passed as environment data, never inserted into executable script text.
    private const string CleanupCommand = """
        $ErrorActionPreference = 'Stop'
        $directory = [IO.Path]::GetFullPath($env:VNOTCH_CLEANUP_DIRECTORY)
        $owner = Get-Process -Id ([int]$env:VNOTCH_CLEANUP_OWNER) -ErrorAction SilentlyContinue
        if ($owner -and -not $owner.WaitForExit(120000)) { exit 1 }
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            if (-not [IO.Directory]::Exists($directory)) { exit 0 }
            $ancestor = [IO.DirectoryInfo]::new($directory)
            while ($null -ne $ancestor) {
                if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                $ancestor = $ancestor.Parent
            }
            try {
                [IO.Directory]::Delete($directory, $true)
                exit 0
            } catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
            Start-Sleep -Milliseconds 500
        }
        exit 1
        """;

    internal static string ValidateDirectory(string directory)
    {
        if (directory == null ||
            vn_install_cleanup_has_valid_input_syntax(directory.AsSpan(), directory.Length, out byte inputOk) != 0 ||
            inputOk == 0 ||
            !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Cleanup requires a fully qualified local installation directory.", nameof(directory));

        var fullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        if (root == null ||
            vn_install_cleanup_has_valid_full_path_segments(fullPath.AsSpan(), fullPath.Length, root.AsSpan(), root.Length, out byte segOk) != 0 ||
            segOk == 0 ||
            new DriveInfo(root).DriveType == DriveType.Network)
            throw new ArgumentException("Invalid installation directory for cleanup.", nameof(directory));

        for (var ancestor = new DirectoryInfo(fullPath); ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Cleanup cannot traverse directory links.", nameof(directory));
        }
        return fullPath;
    }

    internal static ProcessStartInfo CreateStartInfo(string directory, int ownerProcessId)
    {
        var fullPath = ValidateDirectory(directory);
        if (ownerProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerProcessId));
        if (!File.Exists(Path.Combine(fullPath, "V-Notch.exe")))
            throw new InvalidOperationException("The installation directory does not contain V-Notch.exe.");

        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupCommand)));
        startInfo.Environment["VNOTCH_CLEANUP_DIRECTORY"] = fullPath;
        startInfo.Environment["VNOTCH_CLEANUP_OWNER"] = ownerProcessId.ToString(CultureInfo.InvariantCulture);
        return startInfo;
    }

    internal static void Schedule(string directory)
    {
        using var process = Process.Start(CreateStartInfo(directory, Environment.ProcessId));
        if (process == null) throw new InvalidOperationException("Could not start installation cleanup.");
    }
}
