using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed partial class NativeMigrationContractTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("")]
    [InlineData("Tiếng Việt 🎧\0tail")]
    [InlineData("broken\ud800surrogate\udc00")]
    public void CngFramingAndReusableStatePreserveExistingFingerprints(string text)
    {
        text += new string([(char)0xD800, 'x', (char)0xDC00]) + new string('x', 4096);
        byte[] encoded = Encoding.UTF8.GetBytes(text);
        byte[] framed = new byte[4 + encoded.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, encoded.Length);
        encoded.CopyTo(framed, 4);
        using var hash = NativeClipboardHash.Create();
        hash.AppendUtf16(text, framed: true);
        Assert.Equal(SHA256.HashData(framed), hash.GetHashAndReset());
        Assert.Equal(SHA256.HashData(Array.Empty<byte>()), hash.GetHashAndReset());
        hash.AppendData(encoded.AsSpan(0, 17));
        hash.AppendData(encoded.AsSpan(17));
        Assert.Equal(SHA256.HashData(encoded), hash.GetHashAndReset());
    }

    [Fact]
    public unsafe void PixelHashKeepsDimensionsAndExcludesStridePadding()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99, 99, 9, 10, 11, 12, 13, 14, 15, 16, 88, 88, 88, 88];
        byte[] expected = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(4, 4), 2);
        pixels.AsSpan(0, 8).CopyTo(expected.AsSpan(8));
        pixels.AsSpan(12, 8).CopyTo(expected.AsSpan(16));
        Span<byte> digest = stackalloc byte[32];
        fixed (byte* pointer = pixels) NativeClipboardHash.HashPixels((nint)pointer, 2, 2, 12, digest);
        Assert.Equal(SHA256.HashData(expected), digest.ToArray());
    }

    [Fact]
    public void WicImageHashMatchesWpfPixelCanonicalization() => SharedStaTestRunner.Run(() =>
    {
        byte[] bgra = [5, 20, 200, 128, 255, 170, 80, 0, 25, 50, 75, 255, 100, 150, 200, 64];
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, bgra, 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        Span<byte> digest = stackalloc byte[32];
        Assert.True(NativeClipboardHash.TryHashImage(png.ToArray(), null, digest));
        Assert.Equal(ClipboardHistoryStore.FingerprintPixels(bitmap), "image-v2:" + Convert.ToHexString(digest));
    });

    [Fact]
    public void RecorderPollingDoesNotAllocateOnTheManagedHeap()
    {
        var probe = new ScreenRecordingProbe();
        for (int i = 0; i < 5; i++) probe.IsRecording();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) probe.IsRecording();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"native recorder polling: {allocated} managed bytes / 10 polls");
        Assert.Equal(0, allocated);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DeviceState { public int IdOffset, IdLength, NameOffset, NameLength; public byte IsDefault; }
    [LibraryImport("VNotch.Native.dll")] private static partial int vn_audio_devices_open(int flow, out long list, out int count, out int hresult);
    [LibraryImport("VNotch.Native.dll")] private static partial int vn_audio_devices_close(long list);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_get_devices_batch(long list, Span<DeviceState> entries, int capacity, Span<char> text, int textCapacity, out int count, out int textLength);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_audio_device_get_string(long list, int index, int kind, Span<char> text, int capacity, out int length, out byte isDefault);

    [Fact]
    public void DeviceBatchReportsRequiredCapacityAndMatchesTheSameSnapshot()
    {
        Assert.Equal(0, vn_audio_devices_open(0, out long list, out int expectedCount, out _));
        try
        {
            int status = vn_audio_get_devices_batch(list, default, 0, default, 0, out int count, out int chars);
            Assert.Equal(expectedCount == 0 ? 0 : -2, status);
            Assert.Equal(expectedCount, count);
            var entries = new DeviceState[count];
            var text = new char[chars];
            Assert.Equal(0, vn_audio_get_devices_batch(list, entries, entries.Length, text, text.Length, out count, out _));
            Span<char> legacy = stackalloc char[32768];
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(0, vn_audio_device_get_string(list, i, 0, legacy, legacy.Length, out int length, out byte isDefault));
                Assert.Equal(new string(legacy[..length]), new string(text, entries[i].IdOffset, entries[i].IdLength));
                Assert.Equal(isDefault, entries[i].IsDefault);
                Assert.Equal(0, vn_audio_device_get_string(list, i, 1, legacy, legacy.Length, out length, out _));
                Assert.Equal(new string(legacy[..length]), new string(text, entries[i].NameOffset, entries[i].NameLength));
            }
        }
        finally { vn_audio_devices_close(list); }
        Assert.Equal(-1, vn_audio_get_devices_batch(list, default, 0, default, 0, out _, out _));
    }
}
