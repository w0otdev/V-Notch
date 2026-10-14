using System.Collections.Frozen;
using System.IO;
using VNotch.Controllers;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

// Only applies to filesystem discovery. Start Menu apps and explicit pins
// remain available even when their installation lives in one of these roots.
internal static class SpotlightFileVisibility
{
    internal static IReadOnlyList<string> HiddenRoots { get; } = Array.AsReadOnly(new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
    }.Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    internal static FrozenSet<string> HiddenDirectories { get; } = new[]
    {
        "AppData", "$Recycle.Bin", "System Volume Information", ".git", ".svn", ".hg",
        ".vs", ".vscode", ".idea", ".cache", ".nuget", "node_modules", "__pycache__", "obj",
        "Cache", "Caches", "Temp"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static FrozenSet<string> HiddenExtensions { get; } = new[]
    {
        ".dll", ".sys", ".drv", ".ocx", ".pdb", ".obj", ".mui", ".cat",
        ".tmp", ".temp", ".cache", ".etl", ".manifest"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string EverythingExclusionSuffix = " "
        + string.Join(" ", HiddenRoots.Select(root => "!\"" + root + "\\\"")) + " "
        + string.Join(" ", HiddenDirectories.Select(folder => "!\"\\" + folder + "\\\"")) + " "
        + string.Join(" ", HiddenExtensions.Select(extension => "!*" + extension));

    private static readonly string WindowsExclusionScope = string.Concat(HiddenRoots.Select(root =>
        " AND NOT SCOPE='file:" + root.Replace('\\', '/').Replace("'", "''") + "/'"));

    internal static bool IsExplicitPath(string query) =>
        Path.IsPathFullyQualified(query.Trim().Trim('"'));

    internal static bool ShouldSearch(string query) =>
        query.Trim().Length >= 2 || IsExplicitPath(query);

    internal static bool IsFileNameQuery(string query)
    {
        string name = query.Trim().Trim('"');
        return name.Length > 2 && !name.Contains('\\') && !name.Contains('/')
            && name.IndexOfAny(['*', '?', ':', '<', '>', '|']) < 0
            && Path.GetFileNameWithoutExtension(name).Length > 0
            && Path.GetExtension(name).Length > 1;
    }

    internal static bool ShouldInclude(SpotlightSearchItem item, string query)
    {
        if (LocalPathPolicy.IsUncPath(item.Target)) return false;
        string path = item.Target.Replace('/', '\\');
        if (IsExplicitPath(query))
            return path.StartsWith(query.Trim().Trim('"').Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        if (IsFileNameQuery(query) && string.Equals(Path.GetFileName(path), query.Trim().Trim('"'),
            StringComparison.OrdinalIgnoreCase)) return true;

        if (HiddenRoots.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))) return false;
        string[] segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(HiddenDirectories.Contains)) return false;
        return item.Kind == SpotlightResultKind.Folder
            || !HiddenExtensions.Contains(Path.GetExtension(path));
    }

    internal static string BuildEverythingQuery(string query)
    {
        if (IsExplicitPath(query) || IsFileNameQuery(query)) return query;
        // Apply the scope before Everything's result cap, so system files
        // cannot crowd personal documents out of the returned page.
        return "<" + query + ">" + EverythingExclusionSuffix;
    }

    internal static string BuildWindowsScope(string query)
    {
        if (IsExplicitPath(query) || IsFileNameQuery(query)) return string.Empty;
        return WindowsExclusionScope;
    }
}
