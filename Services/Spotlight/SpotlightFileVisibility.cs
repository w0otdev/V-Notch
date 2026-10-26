using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    internal static IReadOnlyList<string> HiddenRoots { get; } = BuildHiddenRoots();

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

    private static readonly string EverythingExclusionSuffix = BuildEverythingExclusionSuffix();

    private static readonly string WindowsExclusionScope = BuildWindowsExclusionScope();

    private static IReadOnlyList<string> BuildHiddenRoots()
    {
        string[] candidates =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>(candidates.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = candidates[i];
            if (string.IsNullOrWhiteSpace(path)) continue;
            string trimmed = Path.TrimEndingDirectorySeparator(path);
            if (seen.Add(trimmed))
            {
                roots.Add(trimmed);
            }
        }

        return Array.AsReadOnly(roots.ToArray());
    }

    private static string BuildEverythingExclusionSuffix()
    {
        var sb = new StringBuilder(512);
        for (int i = 0; i < HiddenRoots.Count; i++)
        {
            sb.Append(" !\"").Append(HiddenRoots[i]).Append("\\\"");
        }

        foreach (string folder in HiddenDirectories)
        {
            sb.Append(" !\"\\").Append(folder).Append("\\\"");
        }

        foreach (string extension in HiddenExtensions)
        {
            sb.Append(" !*").Append(extension);
        }

        return sb.ToString();
    }

    private static string BuildWindowsExclusionScope()
    {
        var sb = new StringBuilder(256);
        for (int i = 0; i < HiddenRoots.Count; i++)
        {
            string escaped = HiddenRoots[i].Replace('\\', '/').Replace("'", "''");
            sb.Append(" AND NOT SCOPE='file:").Append(escaped).Append("/'");
        }

        return sb.ToString();
    }

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
        if (LocalPathPolicy.IsUncPath(rawTarget)) return false;
        string normalizedPath = rawTarget.IndexOf('/') >= 0 ? rawTarget.Replace('/', '\\') : rawTarget;
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
