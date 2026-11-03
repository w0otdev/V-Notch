using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services.Clipboard;

internal sealed partial class NativeClipboardHash : IDisposable
{
    private const string Dll = "VNotch.Native.dll";
    private readonly HashHandle _handle;
    private sealed class HashHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal HashHandle(long value) : base(true) => SetHandle((nint)value);
        protected override bool ReleaseHandle() => vn_clipboard_hash_close(handle.ToInt64()) == 0;
    }
    private NativeClipboardHash(long value) => _handle = new HashHandle(value);
    internal static NativeClipboardHash Create()
    {
        Check(vn_clipboard_hash_create(out long handle));
        return new NativeClipboardHash(handle);
    }
    internal void AppendData(ReadOnlySpan<byte> bytes) => Check(vn_clipboard_hash_append(_handle, bytes, bytes.Length));
    internal void AppendUtf16(string? value, bool framed) => Check(vn_clipboard_hash_append_utf16(_handle, value.AsSpan(), value?.Length ?? 0, framed ? (byte)1 : (byte)0));
    internal void GetHashAndReset(Span<byte> digest) => Check(vn_clipboard_hash_finish(_handle, digest, digest.Length));
    internal byte[] GetHashAndReset() { var digest = new byte[32]; GetHashAndReset(digest); return digest; }
    internal static void HashData(ReadOnlySpan<byte> bytes, Span<byte> digest) => Check(vn_clipboard_hash_bytes(bytes, bytes.Length, digest, digest.Length));
    internal static unsafe void HashPixels(nint pixels, int width, int height, int stride, Span<byte> digest)
        => Check(vn_clipboard_hash_pixels((byte*)pixels, width, height, stride, digest, digest.Length));
    internal static bool TryHashImage(ReadOnlySpan<byte> bytes, string? path, Span<byte> digest)
        => vn_clipboard_hash_image(bytes, bytes.Length, path.AsSpan(), path?.Length ?? 0, digest, digest.Length) == 0;
    public void Dispose() => _handle.Dispose();
    private static void Check(int status) { if (status != 0) throw new CryptographicException($"Native SHA-256 failed ({status})."); }

    [LibraryImport(Dll)] private static partial int vn_clipboard_hash_create(out long handle);
    [LibraryImport(Dll)] private static partial int vn_clipboard_hash_close(long handle);
    [LibraryImport(Dll)] private static partial int vn_clipboard_hash_append(HashHandle handle, ReadOnlySpan<byte> bytes, int length);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_hash_append_utf16(HashHandle handle, ReadOnlySpan<char> text, int length, byte framed);
    [LibraryImport(Dll)] private static partial int vn_clipboard_hash_finish(HashHandle handle, Span<byte> digest, int capacity);
    [LibraryImport(Dll)] private static partial int vn_clipboard_hash_bytes(ReadOnlySpan<byte> bytes, int length, Span<byte> digest, int capacity);
    [LibraryImport(Dll)] private static unsafe partial int vn_clipboard_hash_pixels(byte* pixels, int width, int height, int stride, Span<byte> digest, int capacity);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] private static partial int vn_clipboard_hash_image(ReadOnlySpan<byte> bytes, int length, ReadOnlySpan<char> path, int pathLength, Span<byte> digest, int capacity);
}
