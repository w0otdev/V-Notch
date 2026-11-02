using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace VNotch.Tests;

public sealed partial class ClipboardNativePreviewTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewInfo { public int Width, Height; public double DpiX, DpiY; }
    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_clipboard_image_preview")]
    private static unsafe partial int Decode(char* path, int pathLength, byte* encoded, int encodedLength,
        byte* output, int capacity, out PreviewInfo info);

    [Fact]
    public unsafe void InvalidNativePreviewArgumentsCannotWriteOutput()
    {
        byte[] buffer = Enumerable.Repeat((byte)0xcc, 16).ToArray();
        fixed (byte* output = buffer)
        {
            Assert.Equal(unchecked((int)0x80070057), Decode(null, 0, null, 0, output, buffer.Length, out var info));
            Assert.Equal(default, info);
            byte[] encoded = [1];
            fixed (byte* input = encoded)
            {
                Assert.Equal(unchecked((int)0x80070057), Decode(null, 0, input, -1, output, buffer.Length, out info));
                Assert.Equal(default, info);
            }
        }
        Assert.All(buffer, value => Assert.Equal(0xcc, value));
    }

    [Fact]
    public unsafe void UndersizedNativePreviewBufferRemainsUntouched()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 128 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] png = stream.ToArray(), buffer = Enumerable.Repeat((byte)0xcc, 16).ToArray();
        fixed (byte* input = png)
        fixed (byte* output = buffer)
        {
            Assert.Equal(unchecked((int)0x8007007a), Decode(null, 0, input, png.Length, output, 3, out var info));
            Assert.Equal(default, info);
        }
        Assert.All(buffer, value => Assert.Equal(0xcc, value));
    }
}
