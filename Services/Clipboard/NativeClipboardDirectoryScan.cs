using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VNotch.Interop;

namespace VNotch.Services.Clipboard;

internal sealed unsafe partial class NativeClipboardDirectoryScan : IDisposable
{
    internal readonly record struct Entry(string Path, FileAttributes Attributes, long Size, DateTime Modified);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Row
    {
        internal long Size;
        internal long Modified;
        internal int Offset;
        internal int Length;
        internal uint Attributes;
        internal uint Reserved;
    }

    private sealed class ScanHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ScanHandle() : base(true) { }

        protected override bool ReleaseHandle() =>
            vn_clipboard_directory_scan_close(handle.ToInt64()) == (int)NativeStatus.Ok;
    }

    private readonly ScanHandle _handle;
    private readonly Row* _rows;
    private readonly char* _text;
    internal int Count { get; }

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_directory_scan(
        ReadOnlySpan<char> root,
        int length,
        ReadOnlySpan<char> excluded,
        int excludedLength,
        int limit,
        out ScanHandle handle,
        out Row* rows,
        out int count,
        out char* text,
        out int error);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_directory_scan_close(long handle);

    internal NativeClipboardDirectoryScan(string root, string excluded)
    {
        int status = vn_clipboard_directory_scan(
            root,
            root.Length,
            excluded,
            excluded.Length,
            ClipboardHistoryStore.MaxImportContents + 1,
            out ScanHandle handle,
            out _rows,
            out int count,
            out _text,
            out int error);

        if (status == (int)NativeStatus.LimitExceeded)
        {
            handle.Dispose();
            throw new ClipboardImportLimitException();
        }

        if (status != (int)NativeStatus.Ok)
        {
            handle.Dispose();
            throw new IOException("Could not enumerate the selected directory.", new Win32Exception(error));
        }

        Count = count;
        _handle = handle;
    }

    internal Entry Get(int index)
    {
        bool addedRef = false;
        try
        {
            _handle.DangerousAddRef(ref addedRef);
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Row row = _rows[index];
            return new Entry(
                new string(_text + row.Offset, 0, row.Length),
                (FileAttributes)row.Attributes,
                row.Size,
                DateTime.FromFileTime(row.Modified));
        }
        finally
        {
            if (addedRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
