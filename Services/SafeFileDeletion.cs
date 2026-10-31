using System;
using System.IO;

namespace VNotch.Services;

// Deletion must never follow a user-created junction, including in a parent directory.
internal static class SafeFileDeletion
{
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
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing deletion at an unsafe path.");
        for (string? current = full; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Deletion cannot traverse file or directory links.");
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
