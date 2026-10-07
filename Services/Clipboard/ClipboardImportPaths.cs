using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services.Clipboard;

internal static class ClipboardImportPaths
{
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
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            3, 0x02200000, IntPtr.Zero); // OPEN_EXISTING, OPEN_REPARSE_POINT | BACKUP_SEMANTICS
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8))
            throw new IOException("Cannot inspect the selected linked path.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!IsCloudPlaceholder(info.Tag)) throw new IOException("Symbolic links and junctions cannot be archived.");
        const uint DehydratedMask = 0x00441000; // OFFLINE (0x1000) | RECALL_ON_OPEN (0x40000) | RECALL_ON_DATA_ACCESS (0x400000)
        if ((info.Attributes & DehydratedMask) != 0 || ((uint)attributes & DehydratedMask) != 0)
            throw new IOException("Cloud files not available locally cannot be archived without downloading.");
        return attributes;
    }

    internal static bool IsCloudPlaceholder(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A
        || tag is 0x80000021 or 0x80000015; // CLOUD[_1..F], ONEDRIVE, FILE_PLACEHOLDER

    internal static string DisplayName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return !string.IsNullOrWhiteSpace(name) ? name : "Drive_" + Path.GetPathRoot(path)![0];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { internal uint Attributes; internal uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTag information, uint size);
}
