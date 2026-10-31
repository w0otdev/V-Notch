using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace VNotch.Services;

internal static partial class InstallDirectoryCleanup
{
    internal const string ManifestFileName = "installed-files.json";
    internal static readonly string[] RuntimeLogFileNames =
    [
        "vnotch-debug.log",
        "vnotch-debug.log.old",
        "vnotch-crash.log",
        "vnotch-crash.log.old"
    ];
    internal static readonly string[] RuntimeLogPatterns =
    [
        "vnotch-debug.log*",
        "vnotch-crash.log*"
    ];
    internal static readonly string[] TempInstallerArtifactNames =
    [
        "V-Notch-Setup.exe",
        "V-Notch-Setup-SelfContained.exe",
        "V-Notch-Setup.exe.manifest.json",
        "V-Notch-Setup.exe.sig",
        "V-Notch-Setup-SelfContained.exe.manifest.json",
        "V-Notch-Setup-SelfContained.exe.sig"
    ];
    internal sealed record InstalledFile(string Path, string Sha256);
    internal sealed record InstallManifest(int Version, InstalledFile[] Files);

    internal static string HashFile(string path)
    {
        SafeFileDeletion.ValidatePath(path);
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    internal static InstalledFile[] ReadManifest(string directory)
    {
        string manifest = Path.Combine(directory, ManifestFileName);
        SafeFileDeletion.ValidatePath(manifest);
        if (!File.Exists(manifest)) return [];
        if (new FileInfo(manifest).Length > 16 * 1024 * 1024) throw new IOException("Installation manifest is too large.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        // shortcut: paths-only manifests cannot prove ownership, retain legacy files until a verified upgrade.
        if (document.RootElement.ValueKind == JsonValueKind.Array) return [];
        var value = document.Deserialize<InstallManifest>() ?? throw new IOException("Invalid installation manifest.");
        if (value.Version != 2 || value.Files == null || value.Files.Length > 100_000)
            throw new IOException("Invalid installation manifest.");
        foreach (var file in value.Files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path) || Path.IsPathRooted(file.Path) ||
                file.Path.Split(['/', '\\']).Any(part => part is "" or "." or ".." || part.Contains(':')) ||
                file.Sha256 == null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                file.Path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Invalid installation manifest entry.");
            SafeFileDeletion.ValidatePath(Path.Combine(directory, file.Path));
        }
        return value.Files;
    }
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
        Add-Type -TypeDefinition @'
        using System;
        using System.IO;
        using System.ComponentModel;
        using System.Runtime.InteropServices;
        using System.Security.Cryptography;
        using Microsoft.Win32.SafeHandles;
        public static class VNotchOwnedFile {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref byte delete, uint size);
            public static bool DeleteIfUnchanged(string path, string expected) {
                using (var handle = CreateFile(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero)) {
                    if (handle.IsInvalid) {
                        int code = Marshal.GetLastWin32Error();
                        if (code == 2 || code == 3) return true;
                        throw new IOException("Cannot lock installed file for safe deletion.", new Win32Exception(code));
                    }
                    using (var stream = new FileStream(handle, FileAccess.Read))
                    using (var sha = SHA256.Create()) {
                        string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return false;
                        byte delete = 1;
                        if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
                            throw new IOException("Cannot delete verified installed file.", new Win32Exception(Marshal.GetLastWin32Error()));
                        return true;
                    }
                }
            }
        }
        '@
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            if (-not [IO.Directory]::Exists($directory)) { exit 0 }
            $ancestor = [IO.DirectoryInfo]::new($directory)
            while ($null -ne $ancestor) {
                if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                $ancestor = $ancestor.Parent
            }
            try {
                $manifest = [IO.Path]::Combine($directory, 'installed-files.json')
                if (-not [IO.File]::Exists($manifest)) { exit 0 }
                if (([IO.File]::GetAttributes($manifest) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                if ([IO.FileInfo]::new($manifest).Length -gt 16777216) { exit 1 }
                $data = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($manifest))
                if ($data -is [array] -or $data -is [string]) { exit 0 }
                if ($data.Version -ne 2 -or $data.Files -isnot [array] -or $data.Files.Count -gt 100000) { exit 1 }
                $targets = [Collections.Generic.List[object]]::new()
                $preserved = $false
                foreach ($entry in $data.Files) {
                    $name = $entry.Path
                    if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or
                        $name -eq 'installed-files.json' -or $name -match '(^|[\\/])(\.|\.\.|)([\\/]|$)' -or $name.Contains(':') -or
                        $entry.Sha256 -isnot [string] -or $entry.Sha256 -notmatch '\A[0-9a-fA-F]{64}\z') { exit 1 }
                    $target = [IO.Path]::GetFullPath([IO.Path]::Combine($directory, $name))
                    if (-not $target.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { exit 1 }
                    $ancestor = [IO.FileInfo]::new($target)
                    while ($null -ne $ancestor) {
                        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                        if ($ancestor -is [IO.FileInfo]) { $ancestor = $ancestor.Directory } else { $ancestor = $ancestor.Parent }
                    }
                    $targets.Add([pscustomobject]@{ Path = $target; Hash = $entry.Sha256 })
                }
                foreach ($target in $targets) {
                    # Keep one handle from hashing through deletion; a writer cannot replace verified contents in between.
                    if (-not [VNotchOwnedFile]::DeleteIfUnchanged($target.Path, $target.Hash)) { $preserved = $true }
                }
                foreach ($logName in @('vnotch-debug.log', 'vnotch-debug.log.old', 'vnotch-crash.log', 'vnotch-crash.log.old')) {
                    $logPath = [IO.Path]::Combine($directory, $logName)
                    if ([IO.File]::Exists($logPath)) {
                        if (([IO.File]::GetAttributes($logPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                        [IO.File]::Delete($logPath)
                    }
                }
                foreach ($pattern in @('vnotch-debug.log*', 'vnotch-crash.log*')) {
                    foreach ($logPath in [IO.Directory]::GetFiles($directory, $pattern, [IO.SearchOption]::TopDirectoryOnly)) {
                        if (([IO.File]::GetAttributes($logPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                        [IO.File]::Delete($logPath)
                    }
                }
                if (-not $preserved) { [IO.File]::Delete($manifest) }
                foreach ($target in $targets) {
                    $parent = [IO.Path]::GetDirectoryName($target.Path)
                    while ($parent -and $parent.StartsWith($directory, [StringComparison]::OrdinalIgnoreCase)) {
                        try { [IO.Directory]::Delete($parent, $false) } catch [IO.IOException] { break }
                        if ($parent -eq $directory) { break }
                        $parent = [IO.Path]::GetDirectoryName($parent)
                    }
                }
                if (-not $preserved -and [IO.Directory]::Exists($directory)) {
                    try { [IO.Directory]::Delete($directory, $false) } catch [IO.IOException] { }
                }
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

    internal static string ValidateInstallDirectory(string directory)
    {
        var path = ValidateDirectory(directory);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() &&
            !File.Exists(Path.Combine(path, "V-Notch.exe")))
            throw new ArgumentException("Choose an empty folder or an existing V-Notch installation.", nameof(directory));
        return path;
    }

    internal static ProcessStartInfo CreateStartInfo(string directory, int ownerProcessId)
    {
        var fullPath = ValidateDirectory(directory);
        if (ownerProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerProcessId));
        if (!File.Exists(Path.Combine(fullPath, "V-Notch.exe")))
            throw new InvalidOperationException("The installation directory does not contain V-Notch.exe.");
        ReadManifest(fullPath);

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

    internal static void RemoveRuntimeLogs(string directory, Action<string>? deleteFile = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var fullPath = ValidateDirectory(directory);
        var deleter = deleteFile ?? SafeFileDeletion.DeleteFile;

        foreach (var logName in RuntimeLogFileNames)
        {
            var logPath = Path.Combine(fullPath, logName);
            if (File.Exists(logPath))
            {
                deleter(logPath);
            }
        }

        if (!Directory.Exists(fullPath)) return;
        foreach (var pattern in RuntimeLogPatterns)
        {
            foreach (var logPath in Directory.GetFiles(fullPath, pattern, SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(logPath))
                {
                    deleter(logPath);
                }
            }
        }
    }

    internal static void RemoveTempInstallerArtifacts(string? tempDirectory = null, Action<string>? deleteFile = null)
    {
        var root = string.IsNullOrWhiteSpace(tempDirectory) ? Path.GetTempPath() : Path.GetFullPath(tempDirectory);
        if (!Directory.Exists(root)) return;
        var deleter = deleteFile ?? SafeFileDeletion.DeleteFile;

        foreach (var fileName in TempInstallerArtifactNames)
        {
            var path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                deleter(path);
            }
        }
    }

    internal static void RemoveInstalledAppFiles(string directory, Action<string>? deleteFile = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var fullPath = ValidateDirectory(directory);
        var deleter = deleteFile ?? SafeFileDeletion.DeleteFile;

        RemoveRuntimeLogs(fullPath, deleter);

        var manifestPath = Path.Combine(fullPath, ManifestFileName);
        var targets = ReadManifest(fullPath);
        bool preserved = false;
        foreach (var entry in targets)
        {
            var targetPath = Path.GetFullPath(Path.Combine(fullPath, entry.Path));
            if (!File.Exists(targetPath)) continue;
            if (!HashFile(targetPath).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                preserved = true;
                continue;
            }
            deleter(targetPath);
        }

        if (!preserved && File.Exists(manifestPath))
        {
            deleter(manifestPath);
        }

        foreach (var entry in targets)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fullPath, entry.Path)));
            while (!string.IsNullOrEmpty(parent) && parent.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(parent, recursive: false); }
                catch (IOException) { break; }
                if (string.Equals(parent, fullPath, StringComparison.OrdinalIgnoreCase)) break;
                parent = Path.GetDirectoryName(parent);
            }
        }

        if (!preserved && Directory.Exists(fullPath))
        {
            try { Directory.Delete(fullPath, recursive: false); }
            catch (IOException) { }
        }
    }

    internal static void Schedule(string directory)
    {
        using var process = Process.Start(CreateStartInfo(directory, Environment.ProcessId));
        if (process == null) throw new InvalidOperationException("Could not start installation cleanup.");
    }
}
