using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VNotch.Services.Clipboard;

internal sealed unsafe partial class NativeClipboardDirectoryScan : IDisposable
{
    internal readonly record struct Entry(string Path, FileAttributes Attributes, long Size, DateTime Modified);
    [StructLayout(LayoutKind.Sequential)]
    private struct Row { internal long Size, Modified; internal int Offset, Length; internal uint Attributes, Reserved; }
    private sealed class ScanHandle : SafeHandle
    {
        internal ScanHandle(long value) : base(IntPtr.Zero, true) => SetHandle((nint)value);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => vn_clipboard_directory_scan_close((long)handle) == 0;
    }
    private readonly ScanHandle _handle;
    private readonly Row* _rows;
    private readonly char* _text;
    internal int Count { get; }
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_directory_scan(ReadOnlySpan<char> root, int length, ReadOnlySpan<char> excluded, int excludedLength, int limit,
        out long handle, out Row* rows, out int count, out char* text, out int error);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_directory_scan_close(long handle);

    internal NativeClipboardDirectoryScan(string root, string excluded)
    {
        int status = vn_clipboard_directory_scan(root, root.Length, excluded, excluded.Length, ClipboardHistoryStore.MaxImportContents + 1,
            out long handle, out _rows, out int count, out _text, out int error);
        if (status == -4) throw new ClipboardImportLimitException();
        if (status != 0) throw new IOException("Could not enumerate the selected directory.", new Win32Exception(error));
        Count = count; _handle = new(handle);
    }
    internal Entry Get(int index)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        var row = _rows[index];
        var entry = new Entry(new string(_text + row.Offset, 0, row.Length), (FileAttributes)row.Attributes, row.Size, DateTime.FromFileTime(row.Modified));
        GC.KeepAlive(_handle); return entry;
    }
    public void Dispose() => _handle.Dispose();
}
