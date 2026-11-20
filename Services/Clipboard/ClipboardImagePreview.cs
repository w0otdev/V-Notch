using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardImagePreview
{
    private const int PreviewEdge = 420;
    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewInfo { public int Width, Height; public double DpiX, DpiY; }

    internal readonly record struct PixelTarget(int Width, int Height);

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_clipboard_image_preview_sized", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int NativeDecode(string? path, int pathLength, byte* encoded, int encodedLength,
        byte* pixels, int capacity, int width, int height, out PreviewInfo info);

    internal static BitmapSource Decode(byte[] encoded, bool isPersonal, CancellationToken cancellation, PixelTarget target = default)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return DecodeCore(null, encoded, isPersonal, cancellation, target);
    }

    internal static BitmapSource Decode(Stream stream, bool isPersonal, CancellationToken cancellation, PixelTarget target = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellation.ThrowIfCancellationRequested();
        if (!stream.CanRead) throw new NotSupportedException("Preview input must be readable.");
        if (stream is FileStream file) return DecodeCore(file.Name, [], isPersonal, cancellation, target);
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var buffer))
            return DecodeCore(null, buffer.AsSpan(), isPersonal, cancellation, target);
        if (!stream.CanSeek) throw new NotSupportedException("Preview input must be seekable.");
        int length = checked((int)stream.Length);
        // Large encoded images must not leave a full PNG-sized bucket in the
        // shared pool. Scratch storage is wiped and freed as soon as WIC exits.
        using var encoded = new NativeBufferStream();
        encoded.SetLength(length);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            stream.Position = 0;
            for (int remaining = length; remaining > 0;)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = Math.Min(remaining, chunk.Length);
                stream.ReadExactly(chunk.AsSpan(0, count));
                encoded.Write(chunk.AsSpan(0, count));
                remaining -= count;
            }
            return encoded.ReadBuffered(bytes => DecodeCore(null, bytes, isPersonal, cancellation, target));
        }
        finally
        {
            // Custom streams can contain decrypted public as well as private data.
            CryptographicOperations.ZeroMemory(chunk);
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static unsafe BitmapSource DecodeCore(string? path, ReadOnlySpan<byte> encoded, bool isPersonal, CancellationToken cancellation, PixelTarget target = default)
    {
        cancellation.ThrowIfCancellationRequested();
        int edge = Math.Clamp(Math.Max(target.Width, target.Height), PreviewEdge, 4096);
        byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(edge * edge * 4));
        try
        {
            PreviewInfo info;
            int result;
            fixed (byte* input = encoded)
            fixed (byte* output = pixels)
                result = NativeDecode(path, path?.Length ?? 0, input, encoded.Length, output, pixels.Length, target.Width, target.Height, out info);
            if (result < 0)
            {
                var error = Marshal.GetExceptionForHR(result);
                if (error is IOException or UnauthorizedAccessException or OutOfMemoryException) throw error;
                throw new FileFormatException("Cannot decode image preview.", error);
            }
            cancellation.ThrowIfCancellationRequested();
            // WPF sees only the detached preview, not a full-size decoder's GC memory-pressure estimate.
            var preview = BitmapSource.Create(info.Width, info.Height, info.DpiX, info.DpiY,
                PixelFormats.Bgra32, null, pixels, checked(info.Width * 4));
            preview.Freeze();
            cancellation.ThrowIfCancellationRequested();
            return preview;
        }
        finally
        {
            if (isPersonal) CryptographicOperations.ZeroMemory(pixels);
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
