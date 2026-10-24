using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

// Only applies to filesystem discovery. Start Menu apps and explicit pins
// remain available even when their installation lives in one of these roots.
internal static partial class SpotlightFileVisibility
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_visibility_is_explicit_path(
        ReadOnlySpan<char> queryPtr, int queryLen,
        out byte outResult);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_visibility_should_search(
        ReadOnlySpan<char> queryPtr, int queryLen,
        out byte outResult);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_visibility_is_file_name_query(
        ReadOnlySpan<char> queryPtr, int queryLen,
        out byte outResult);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_visibility_should_include(
        ReadOnlySpan<char> targetPtr, int targetLen,
        ReadOnlySpan<char> queryPtr, int queryLen,
        byte isFolder,
        byte isUnderHiddenRoot,
        out byte outResult);

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

    internal static bool IsExplicitPath(string query)
    {
        ReadOnlySpan<char> q = (query ?? string.Empty).AsSpan();
        vn_spotlight_visibility_is_explicit_path(q, q.Length, out byte res);
        return res != 0;
    }

    internal static bool ShouldSearch(string query)
    {
        ReadOnlySpan<char> q = (query ?? string.Empty).AsSpan();
        vn_spotlight_visibility_should_search(q, q.Length, out byte res);
        return res != 0;
    }

    internal static bool IsFileNameQuery(string query)
    {
        ReadOnlySpan<char> q = (query ?? string.Empty).AsSpan();
        vn_spotlight_visibility_is_file_name_query(q, q.Length, out byte res);
        return res != 0;
    }

    internal static bool ShouldInclude(SpotlightSearchItem item, string query)
    {
        string rawTarget = item.Target ?? string.Empty;
        string normalizedPath = rawTarget.Replace('/', '\\');
        bool underHiddenRoot = false;
        for (int i = 0; i < HiddenRoots.Count; i++)
        {
            string root = HiddenRoots[i];
            if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                (normalizedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                 normalizedPath.Length > root.Length &&
                 normalizedPath[root.Length] == '\\'))
            {
                underHiddenRoot = true;
                break;
            }
        }

        ReadOnlySpan<char> t = rawTarget.AsSpan();
        ReadOnlySpan<char> q = (query ?? string.Empty).AsSpan();
        vn_spotlight_visibility_should_include(
            t, t.Length,
            q, q.Length,
            item.Kind == SpotlightResultKind.Folder ? (byte)1 : (byte)0,
            underHiddenRoot ? (byte)1 : (byte)0,
            out byte res);
        return res != 0;
    }

    internal static string BuildEverythingQuery(string query)
    {
        if (IsExplicitPath(query) || IsFileNameQuery(query)) return query;
        return "<" + query + ">" + EverythingExclusionSuffix;
    }

    internal static string BuildWindowsScope(string query)
    {
        if (IsExplicitPath(query) || IsFileNameQuery(query)) return string.Empty;
        return WindowsExclusionScope;
    }
}
