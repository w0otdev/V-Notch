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

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_clipboard_image_preview", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int NativeDecode(string? path, int pathLength, byte* encoded, int encodedLength,
        byte* pixels, int capacity, out PreviewInfo info);

    internal static BitmapSource Decode(byte[] encoded, bool isPersonal, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return DecodeCore(null, encoded, isPersonal, cancellation);
    }

    internal static BitmapSource Decode(Stream stream, bool isPersonal, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellation.ThrowIfCancellationRequested();
        if (!stream.CanRead) throw new NotSupportedException("Preview input must be readable.");
        if (stream is FileStream file) return DecodeCore(file.Name, [], isPersonal, cancellation);
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var buffer))
            return DecodeCore(null, buffer.AsSpan(), isPersonal, cancellation);
        if (!stream.CanSeek) throw new NotSupportedException("Preview input must be seekable.");
        int length = checked((int)stream.Length);
        byte[] encoded = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.Position = 0;
            stream.ReadExactly(encoded.AsSpan(0, length));
            return DecodeCore(null, encoded.AsSpan(0, length), isPersonal, cancellation);
        }
        finally
        {
            if (isPersonal) CryptographicOperations.ZeroMemory(encoded);
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    private static unsafe BitmapSource DecodeCore(string? path, ReadOnlySpan<byte> encoded, bool isPersonal, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        byte[] pixels = ArrayPool<byte>.Shared.Rent(PreviewEdge * PreviewEdge * 4);
        try
        {
            PreviewInfo info;
            int result;
            fixed (byte* input = encoded)
            fixed (byte* output = pixels)
                result = NativeDecode(path, path?.Length ?? 0, input, encoded.Length, output, pixels.Length, out info);
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
