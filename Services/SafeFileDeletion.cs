using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace VNotch.Services;

// Deletion must never follow a user-created junction, including in a parent directory.
internal static class SafeFileDeletion
{
    private const uint ReparseTagNameSurrogateBit = 0x20000000u;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindDataW
    {
        public uint dwFileAttributes;
        public uint ftCreationTimeLow;
        public uint ftCreationTimeHigh;
        public uint ftLastAccessTimeLow;
        public uint ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow;
        public uint ftLastWriteTimeHigh;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out Win32FindDataW lpFindFileData);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    // IsReparseTagNameSurrogate (bit 29) identifies symlinks (0xA000000C) and junctions/mount points (0xA0000003)
    // while allowing non-surrogate OneDrive / Cloud Files placeholders (0x9000001A..0x9000F01A).
    internal static bool IsBlockedReparseTag(uint reparseTag) =>
        reparseTag <= 1 || (reparseTag & ReparseTagNameSurrogateBit) != 0;

    private static bool HasBlockedReparsePoint(string current)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(current);
        string? root = Path.GetPathRoot(current);
        if (root != null && string.Equals(trimmed, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return true;

        IntPtr handle = FindFirstFileW(trimmed, out Win32FindDataW data);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err is not (ErrorFileNotFound or ErrorPathNotFound);
        }

        try
        {
            if ((data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) == 0)
                return false;
            return IsBlockedReparseTag(data.dwReserved0);
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    internal static bool PathsOverlap(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidatePath(string path)
    {
        string full = Path.GetFullPath(path);
        string trimmedFull = Path.TrimEndingDirectorySeparator(full);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.Equals(trimmedFull, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing deletion at an unsafe path.");
        for (string? current = trimmedFull; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 &&
                    HasBlockedReparsePoint(current))
                {
                    throw new IOException("Deletion cannot traverse file or directory links.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void DeleteFile(string path)
    {
        ValidatePath(path);
        File.Delete(path);
    }

    internal static void DeleteDirectory(string path)
    {
        ValidatePath(path);
        if (!Directory.Exists(path)) return;
        var directories = new List<string> { path };
        var files = new List<string>();
        for (int i = 0; i < directories.Count; i++)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directories[i]))
            {
                ValidatePath(entry);
                if (Directory.Exists(entry)) directories.Add(entry);
                else files.Add(entry);
            }
        }
        // Check the whole tree before deleting anything; a linked child must not cause a partial wipe.
        foreach (string file in files) DeleteFile(file);
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            ValidatePath(directories[i]);
            Directory.Delete(directories[i], recursive: false);
        }
    }

    internal static void DeleteShortcutDirectory(string path)
    {
        ValidatePath(path);
        if (!Directory.Exists(path)) return;
        DeleteFile(Path.Combine(path, "V-Notch.lnk"));
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path, recursive: false);
    }
}
