using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VNotch.Interop;

namespace VNotch.Services.Clipboard;

// Native owns DPAPI/CNG keys, record framing, commit selection, mappings and durability.
internal sealed partial class NativeClipboardRecordStore : IDisposable
{
    private readonly RecordHandle _handle;

    private sealed class RecordHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RecordHandle() : base(true) { }

        protected override bool ReleaseHandle() =>
            vn_clipboard_record_close(handle.ToInt64()) == (int)NativeStatus.Ok;
    }

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_open(string root, int length, out RecordHandle handle);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_clipboard_record_close(long handle);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_migrate(RecordHandle handle, string path, int length);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_read(RecordHandle handle, string path, int length,
        Span<byte> buffer, int capacity, out int written);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_append(RecordHandle handle, string path, int pathLength,
        ReadOnlySpan<byte> payload, int length);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StartupItem
    {
        public int DirectoryOffset, DirectoryLength, Status, PayloadLength, MigrationStatus;
    }

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_startup_batch(RecordHandle handle,
        string directories, int directoryChars, Span<StartupItem> items, int count, Span<byte> payloads, int capacity);

    internal delegate void StartupRecordReader(string directory, ReadOnlySpan<byte> json, bool needsFallback, bool migrationDeferred);

    // The callback must consume the borrowed plaintext before returning. The
    // fixed batch bounds peak memory regardless of the number of pinned items.
    internal void ReadStartupBatch(IReadOnlyList<string> directories, StartupRecordReader read)
    {
        const int stride = 4096;
        if (directories.Count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(directories));
        Span<StartupItem> items = stackalloc StartupItem[directories.Count];
        string paths = string.Join('\0', directories);
        int offset = 0;
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new() { DirectoryOffset = offset, DirectoryLength = directories[i].Length };
            offset += directories[i].Length + 1;
        }
        byte[] payloads = ArrayPool<byte>.Shared.Rent(items.Length * stride);
        try
        {
            if (vn_clipboard_record_startup_batch(_handle, paths, paths.Length, items, items.Length,
                payloads, payloads.Length) != (int)NativeStatus.Ok) throw new IOException("Could not read clipboard startup metadata.");
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                bool valid = item.Status == (int)NativeStatus.Ok && item.PayloadLength is > 0 and <= stride;
                read(directories[i], valid ? payloads.AsSpan(i * stride, item.PayloadLength) : [],
                    needsFallback: !valid, migrationDeferred: item.MigrationStatus != 0);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloads);
            ArrayPool<byte>.Shared.Return(payloads);
        }
    }

    internal NativeClipboardRecordStore(string root)
    {
        if (vn_clipboard_record_open(root, root.Length, out RecordHandle handle) != (int)NativeStatus.Ok)
        {
            handle.Dispose();
            throw new IOException("Could not open the clipboard record store.");
        }

        _handle = handle;
    }
    // null identifies an existing legacy JSON document.
    internal byte[]? Read(string path)
    {
        TryRead<byte[]>(path, static bytes => bytes.ToArray(), out var payload);
        return payload;
    }
    internal bool TryReadJson<T>(string path, out T? value) =>
        TryRead<T?>(path, static bytes => JsonSerializer.Deserialize<T>(bytes), out value);

    private delegate T PayloadReader<T>(ReadOnlySpan<byte> bytes);
    private bool TryRead<T>(string path, PayloadReader<T> parse, out T? value)
    {
        // Entry metadata normally fits on the stack: one native call and no managed
        // plaintext array. Large content uses a wiped pool buffer and retries the read.
        Span<byte> buffer = stackalloc byte[4096];
        byte[]? rented = null;
        value = default;
        try
        {
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                int status = vn_clipboard_record_read(_handle, path, path.Length, buffer, buffer.Length, out int written);
                if (status == 1) return false;
                if (status == 2) throw new FileNotFoundException("The clipboard record does not exist.", path);
                if (status == (int)NativeStatus.Ok && written > 0 && written <= buffer.Length)
                {
                    value = parse(buffer[..written]);
                    return true;
                }
                if (status != (int)NativeStatus.BufferTooSmall || written <= buffer.Length || written > 256 * 1024 * 1024)
                    throw new IOException("Clipboard record authentication or read failed.");
                CryptographicOperations.ZeroMemory(buffer);
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                rented = null;
                buffer = [];
                rented = ArrayPool<byte>.Shared.Rent(written);
                buffer = rented;
            }
            throw new IOException("The clipboard record kept growing during the read.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
    internal void Append(string path, ReadOnlySpan<byte> payload)
    {
        if (vn_clipboard_record_append(_handle, path, path.Length, payload, payload.Length) != (int)NativeStatus.Ok)
            throw new IOException("Could not commit the clipboard record.");
    }
    internal void Migrate(string path)
    {
        if (vn_clipboard_record_migrate(_handle, path, path.Length) != (int)NativeStatus.Ok)
            throw new IOException("Could not migrate the clipboard record.");
    }
    public void Dispose() => _handle.Dispose();
}
