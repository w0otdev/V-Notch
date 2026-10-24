using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardImportPaths
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_is_cloud_placeholder(uint tag, out byte outIsCloud);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_validate_reparse_point(
        ReadOnlySpan<char> pathPtr,
        int pathLen,
        uint fileAttributes,
        out int outErrorKind,
        out int outWin32Error);

    internal static bool IsLocal(string path) => !LocalPathPolicy.IsUncPath(path)
        && !string.IsNullOrWhiteSpace(path) && !path.Contains('\0')
        && !path.StartsWith(@"\??\", StringComparison.Ordinal);

    internal static string Normalize(string path)
    {
        if (!IsLocal(path)) throw new IOException("Only local paths can be saved in history.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw new IOException("The selected path is invalid.", ex); }
        if (!IsLocal(full) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new IOException("Network drives cannot be saved in history.");
        // Check ancestors before touching a descendant: a local-looking path can
        // sit under a junction or a symbolic link pointing at an SMB server.
        var parents = new Stack<string>();
        for (string? current = full; current != null; current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
            parents.Push(current);
        while (parents.TryPop(out string? current)) ValidateEntry(current);
        return full;
    }

    internal static FileAttributes ValidateEntry(string path)
    {
        if (!IsLocal(path)) throw new IOException("Network paths cannot be saved in history.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return attributes;

        ReadOnlySpan<char> span = path.AsSpan();
        vn_clipboard_validate_reparse_point(span, span.Length, (uint)attributes, out int errKind, out int win32Err);
        return errKind switch
        {
            1 => throw new IOException("Cannot inspect the selected linked path.", new Win32Exception(win32Err)),
            2 => throw new IOException("Symbolic links and junctions cannot be archived."),
            3 => throw new IOException("Cloud files not available locally cannot be archived without downloading."),
            _ => attributes
        };
    }

    internal static bool IsCloudPlaceholder(uint tag)
    {
        vn_clipboard_is_cloud_placeholder(tag, out byte isCloud);
        return isCloud != 0;
    }

    internal static string DisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return !string.IsNullOrWhiteSpace(name) ? name : "Drive_" + Path.GetPathRoot(path)![0];
    }
}
