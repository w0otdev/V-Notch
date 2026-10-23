using System.Diagnostics;
using System.IO;

namespace VNotch.Services;

internal static class AppPaths
{
    internal const string DirectoryName = "V-Notch";
    internal const string LegacyDirectoryName = "VNotch";
    internal static string LocalAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirectoryName);
    internal static string LegacyLocalAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDirectoryName);
    internal static string RoamingAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DirectoryName);

    // Run before stores open files. Moves within LocalAppData preserve model hardlinks
    // and avoid copying multi-gigabyte models. Tests supply an isolated parent directory.
    internal static void MigrateLegacyLocalData(string? localAppData = null)
    {
        string parent = Path.GetFullPath(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string source = Path.Combine(parent, LegacyDirectoryName);
        string destination = Path.Combine(parent, DirectoryName);
        if (!Directory.Exists(source)) return;
        try
        {
            EnsureOrdinaryAncestors(source);
            EnsureOrdinaryAncestors(destination);
            MoveDirectory(source, destination, destination, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Legacy V-Notch data migration deferred: {0}", ex.GetType().Name);
        }
    }

    private static void MoveDirectory(string source, string destination, string canonicalRoot, string relativePath)
    {
        EnsureOrdinaryAncestors(source);
        EnsureOrdinaryAncestors(destination);
        if (File.Exists(destination))
            destination = ConflictDestination(canonicalRoot, relativePath);
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            try
            {
                // Never follow a junction or symbolic link out of either owned root.
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = Path.Combine(relativePath, Path.GetFileName(entry));
                string target = Path.Combine(destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                    MoveDirectory(entry, target, canonicalRoot, relative);
                else
                {
                    EnsureOrdinaryAncestors(target);
                    if (File.Exists(target) || Directory.Exists(target))
                        target = ConflictDestination(canonicalRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(entry, target); // No overwrite; failed moves leave the source intact.
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("A legacy V-Notch data entry could not be migrated: {0}", ex.GetType().Name);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private static string ConflictDestination(string canonicalRoot, string relativePath)
    {
        string target = Path.Combine(canonicalRoot, "legacy-migration-conflicts", relativePath);
        EnsureOrdinaryAncestors(target);
        if (File.Exists(target) || Directory.Exists(target)) target += "." + Guid.NewGuid().ToString("N");
        return target;
    }

    private static void EnsureOrdinaryAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("App data migration does not follow reparse points.");
        }
    }
}
