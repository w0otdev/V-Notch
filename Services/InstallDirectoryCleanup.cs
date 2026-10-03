using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VNotch.Services;

internal static class InstallDirectoryCleanup
{
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
        if (string.IsNullOrWhiteSpace(directory) || directory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\") || directory.StartsWith("//"))
            throw new ArgumentException("Cleanup requires a fully qualified local installation directory.", nameof(directory));

        var fullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath);
        if (root == null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            fullPath.Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(root).DriveType == DriveType.Network ||
            fullPath[3..].Split('\\', '/').Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
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
