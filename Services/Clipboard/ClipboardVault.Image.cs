using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services.Clipboard;

internal sealed partial class ClipboardVault
{
    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_vault_load_image(SafeFileHandle file, ReadOnlySpan<byte> key, int keyLength,
        out ImageBuffer handle, out int length);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_vault_read_image(ImageBuffer handle, int offset, Span<byte> output, int count);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_vault_close_image(long handle);

    private sealed class ImageBuffer() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => vn_clipboard_vault_close_image(handle.ToInt64()) == 0;
        internal void CopyTo(int offset, Span<byte> output)
        {
            if (vn_clipboard_vault_read_image(this, offset, output, output.Length) != 0)
                throw new IOException("Could not read authenticated image data.");
        }
    }

    private ImageBuffer OpenImage(FileStream input, out int length)
    {
        if (vn_clipboard_vault_load_image(input.SafeFileHandle, Key, Key.Length, out var owner, out length) == 0) return owner;
        owner.Dispose();
        throw new CryptographicException("Invalid Personal image data.");
    }

    internal byte[] ReadImage(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        using var owner = OpenImage(input, out int imageLength);
        byte[]? result = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            result = new byte[imageLength];
            // One final managed array is required by GetImageAsync's public contract.
            owner.CopyTo(0, result);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch { if (result != null) CryptographicOperations.ZeroMemory(result); throw; }
    }

    internal void CopyImage(string path, Stream output)
    {
        using var input = File.OpenRead(path);
        using var owner = OpenImage(input, out int imageLength);
        Span<byte> buffer = stackalloc byte[8192];
        try
        {
            for (int offset = 0; offset < imageLength;)
            {
                var chunk = buffer[..Math.Min(buffer.Length, imageLength - offset)];
                owner.CopyTo(offset, chunk);
                output.Write(chunk);
                offset += chunk.Length;
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
