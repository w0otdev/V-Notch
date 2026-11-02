using System.Collections;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardImagePreviewTests
{
    [Theory]
    [InlineData(1800, 1200, 420, 280)]
    [InlineData(1200, 1800, 280, 420)]
    [InlineData(997, 701, 420, 295)]
    [InlineData(13, 7, 13, 7)]
    public void NativePreviewMatchesWpfFantScalingAndKeepsAlphaAndDpi(int width, int height, int expectedWidth, int expectedHeight)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                pixels[p] = (byte)(x / 3); pixels[p + 1] = (byte)(y / 2);
                pixels[p + 2] = (byte)((x + y) / 5); pixels[p + 3] = (byte)(32 + x % 224);
            }
        var source = BitmapSource.Create(width, height, 120, 144, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var encoded = new MemoryStream(); encoder.Save(encoded);
        byte[] png = encoded.ToArray();
        var actual = ClipboardImagePreview.Decode(png, false, CancellationToken.None);
        using var referenceInput = new MemoryStream(png);
        var reference = new BitmapImage();
        reference.BeginInit(); reference.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        reference.CacheOption = BitmapCacheOption.OnLoad; reference.StreamSource = referenceInput;
        if (width >= height) reference.DecodePixelWidth = Math.Min(420, width);
        else reference.DecodePixelHeight = Math.Min(420, height);
        reference.EndInit();
        var expected = new FormatConvertedBitmap(reference, PixelFormats.Bgra32, null, 0);
        Assert.Equal((expectedWidth, expectedHeight), (actual.PixelWidth, actual.PixelHeight));
        Assert.Equal(reference.DpiX, actual.DpiX, 6); Assert.Equal(reference.DpiY, actual.DpiY, 6);
        Assert.True(actual.IsFrozen);
        byte[] expectedPixels = new byte[expectedWidth * expectedHeight * 4], actualPixels = new byte[expectedPixels.Length];
        expected.CopyPixels(expectedPixels, expectedWidth * 4, 0); actual.CopyPixels(actualPixels, expectedWidth * 4, 0);
        Assert.All(expectedPixels.Zip(actualPixels), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1));
    }

    [Fact]
    public void NativePreviewIsDetachedFromEncodedBytesAndReleasesItsFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "vnotch-detached-preview-" + Guid.NewGuid().ToString("N") + ".png");
        byte[] png = Png(800, 600, 80);
        try
        {
            File.WriteAllBytes(file, png);
            BitmapSource fromFile;
            using (var input = File.OpenRead(file)) fromFile = ClipboardImagePreview.Decode(input, false, CancellationToken.None);
            var fromMemory = ClipboardImagePreview.Decode(png, true, CancellationToken.None);
            Array.Clear(png);
            using (var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                byte[] a = new byte[420 * 315 * 4], b = new byte[a.Length];
                fromFile.CopyPixels(a, 420 * 4, 0); fromMemory.CopyPixels(b, 420 * 4, 0);
                Assert.Equal(a, b);
                Assert.Contains(a, value => value != 0);
            }
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void NativePreviewRejectsMalformedAndOversizedImagesAndHonorsCancellation()
    {
        Assert.Throws<FileFormatException>(() => ClipboardImagePreview.Decode([1, 2, 3, 4], false, CancellationToken.None));
        byte[] oversized = Png(1, 1, 80);
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 65536);
        uint crc = uint.MaxValue;
        foreach (byte value in oversized.AsSpan(12, 17))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(29, 4), ~crc);
        Assert.Throws<FileFormatException>(() => ClipboardImagePreview.Decode(oversized, false, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ClipboardImagePreview.Decode([1, 2, 3, 4], true, cancelled.Token));
    }

    [Fact]
    public async Task ScrollingBackReusesDetachedPreviewWithoutReadingOriginalAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            byte[] original = Png(1800, 1200, 80);
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = original })).Entry!;
            var preview = await store.GetImagePreviewAsync(entry.Id);
            Assert.NotNull(preview);
            Assert.True(preview.IsFrozen);
            Assert.Equal(420, preview.PixelWidth);
            Assert.True(preview.PixelHeight <= 420);
            string path = Path.Combine(root, "items", entry.Id.ToString("N"), "image.png");
            // The preview must be detached, and revisiting it must not reopen
            // the full PNG or repeat decoding when a card is recycled.
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                for (int i = 0; i < 30; ++i) Assert.Same(preview, await store.GetImagePreviewAsync(entry.Id));
                Console.WriteLine($"30 warm preview loads: {(GC.GetTotalAllocatedBytes(true) - before) / 1024.0:F1} KiB managed allocation");
            }
            Assert.Equal(original, await store.GetImageAsync(entry.Id));
            await store.DeleteAsync(entry.Id);
            Assert.Empty(Cache(store));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PreviewCacheStaysWithinItsPixelBudgetWhileLoadingMoreCards()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-preview-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            for (byte i = 0; i < 20; ++i)
            {
                var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = Png(420, 420, (byte)(i * 11)) })).Entry!;
                Assert.NotNull(await store.GetImagePreviewAsync(entry.Id));
            }
            Assert.InRange(Cache(store).Count, 1, 24);
            long bytes = (long)typeof(ClipboardHistoryStore).GetField("_imagePreviewBytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
            Assert.InRange(bytes, 1, 8 * 1024 * 1024);
            Assert.True(Cache(store).Count < 20, "Pixel-budget eviction must run before the item-count limit.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MovingToPersonalEvictsPublicPreviewAndPrivateLoadsAreNotCached()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-preview-private-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = Png(64, 64, 120) })).Entry!;
            Assert.NotNull(await store.GetImagePreviewAsync(entry.Id));
            using var pin = new SecureString();
            foreach (char c in "123456") pin.AppendChar(c);
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));
            await store.MovePersonalAsync(entry.Id, true);
            Assert.Empty(Cache(store));
            var first = await store.GetImagePreviewAsync(entry.Id);
            Assert.NotNull(first);
            Assert.NotSame(first, await store.GetImagePreviewAsync(entry.Id));
            Assert.Empty(Cache(store));
            await store.LockPersonalAsync();
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetImagePreviewAsync(entry.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IDictionary Cache(ClipboardHistoryStore store) =>
        (IDictionary)typeof(ClipboardHistoryStore).GetField("_imagePreviews", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

    private static byte[] Png(int width, int height, byte color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color; pixels[i + 1] = (byte)(255 - color); pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
