using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services.Clipboard;

internal sealed partial class ClipboardVault
{
    [LibraryImport(NativeDll)]
    private static unsafe partial int vn_clipboard_vault_open_image(SafeFileHandle file, ReadOnlySpan<byte> key, int keyLength,
        out long handle, out byte* bytes, out int length);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_vault_close_image(long handle);

    private sealed class ImageBuffer() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => vn_clipboard_vault_close_image(handle.ToInt64()) == 0;
        internal void Initialize(long value) => SetHandle((IntPtr)value);
    }

    private unsafe ImageBuffer OpenImage(FileStream input, out IntPtr pointer, out int length)
    {
        var owner = new ImageBuffer();
        try
        {
            if (vn_clipboard_vault_open_image(input.SafeFileHandle, Key, Key.Length, out long handle, out byte* bytes, out length) != 0)
                throw new CryptographicException("Invalid Personal image data.");
            owner.Initialize(handle);
            pointer = (IntPtr)bytes;
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    internal unsafe byte[] ReadImage(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        using var owner = OpenImage(input, out IntPtr pointer, out int imageLength);
        byte[]? result = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            result = new byte[imageLength];
            // One final managed array is required by GetImageAsync's public contract.
            new ReadOnlySpan<byte>((void*)pointer, imageLength).CopyTo(result);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch { if (result != null) CryptographicOperations.ZeroMemory(result); throw; }
    }

    internal unsafe void CopyImage(string path, Stream output)
    {
        using var input = File.OpenRead(path);
        using var owner = OpenImage(input, out IntPtr pointer, out int imageLength);
        // Write directly from authenticated native memory, without a managed staging array.
        for (int offset = 0; offset < imageLength;)
        {
            int length = Math.Min(64 * 1024, imageLength - offset);
            output.Write(new ReadOnlySpan<byte>((byte*)pointer + offset, length));
            offset += length;
        }
    }
}
