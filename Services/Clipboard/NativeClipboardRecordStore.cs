using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace VNotch.Services.Clipboard;

// Native owns DPAPI/CNG keys, record framing, commit selection, mappings and durability.
internal sealed partial class NativeClipboardRecordStore : IDisposable
{
    private readonly RecordHandle _handle;
    private sealed class RecordHandle : SafeHandle
    {
        public RecordHandle() : base(IntPtr.Zero, true) { }
        internal RecordHandle(long value) : this() => SetHandle((nint)value);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => vn_clipboard_record_close((long)handle) == 0;
    }
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_open(string root, int length, out long handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_clipboard_record_close(long handle);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_migrate(RecordHandle handle, string path, int length);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_read(RecordHandle handle, string path, int length,
        Span<byte> buffer, int capacity, out int written);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_record_append(RecordHandle handle, string path, int pathLength,
        ReadOnlySpan<byte> payload, int length);

    internal NativeClipboardRecordStore(string root)
    {
        if (vn_clipboard_record_open(root, root.Length, out long handle) != 0)
            throw new IOException("Could not open the clipboard record store.");
        _handle = new RecordHandle(handle);
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
                if (status == 0 && written > 0 && written <= buffer.Length)
                {
                    value = parse(buffer[..written]);
                    return true;
                }
                if (status != -2 || written <= buffer.Length || written > 256 * 1024 * 1024)
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
        if (vn_clipboard_record_append(_handle, path, path.Length, payload, payload.Length) != 0)
            throw new IOException("Could not commit the clipboard record.");
    }
    internal void Migrate(string path)
    {
        if (vn_clipboard_record_migrate(_handle, path, path.Length) != 0)
            throw new IOException("Could not migrate the clipboard record.");
    }
    public void Dispose() => _handle.Dispose();
}
