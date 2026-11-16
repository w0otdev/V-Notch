using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardMemoryOptimizationTests
{
    [Fact]
    public async Task SerializedEncodingPreservesOriginalPixelsDpiAlphaAndFingerprint()
    {
        var image = Image(797, 613);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var expected = new MemoryStream();
        encoder.Save(expected);
        string fingerprint = ClipboardHistoryStore.FingerprintPixels(image);
        var captures = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            ClipboardHistoryController.EncodeImageAsync(new ClipboardCapture { Text = $"capture {i}" }, image)));
        for (int i = 0; i < captures.Length; i++)
        {
            Assert.Equal($"capture {i}", captures[i].Text);
            Assert.Equal(expected.ToArray(), captures[i].ImagePng);
            Assert.Equal(fingerprint, captures[i].ImagePixelFingerprint);
        }
        // A failed encode must not hold the gate and block subsequent captures.
        await Assert.ThrowsAnyAsync<Exception>(() => ClipboardHistoryController.EncodeImageAsync(new(), null!));
        var next = await ClipboardHistoryController.EncodeImageAsync(new(), image).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected.ToArray(), next.ImagePng);
    }

    [Fact]
    public void CustomStreamPreviewIsPixelIdenticalAndChecksCancellationBetweenChunks()
    {
        byte[] png = Png(Image(997, 701));
        var expected = ClipboardImagePreview.Decode(png, false, default);
        using var input = new ChunkedInput(png);
        var actual = ClipboardImagePreview.Decode(input, true, default);
        Assert.Equal(Pixels(expected), Pixels(actual));
        Assert.Equal(expected.DpiX, actual.DpiX);
        Assert.Equal(expected.DpiY, actual.DpiY);
        Assert.True(actual.IsFrozen);
        Assert.True(input.CanRead);
        Assert.InRange(input.LargestRead, 1, 64 * 1024);
        using var cancellation = new CancellationTokenSource();
        using var cancelling = new ChunkedInput(png, cancellation);
        Assert.Throws<OperationCanceledException>(() => ClipboardImagePreview.Decode(cancelling, true, cancellation.Token));
        using var truncated = new ChunkedInput(png, reportedExtraBytes: 1);
        Assert.Throws<EndOfStreamException>(() => ClipboardImagePreview.Decode(truncated, false, default));
    }

    [Fact]
    public void TextCacheTrimKeepsRecentEntriesAndHonorsBothBudgets()
    {
        var cache = new ClipboardSearchTextCache(100, 10);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        cache.Set(first, "first"); cache.Set(second, "second"); cache.Set(third, "third");
        Assert.True(cache.TryGet(first, out _));
        cache.Trim(10, 2);
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(first, out var text)); Assert.Equal("first", text);
        Assert.True(cache.TryGet(third, out _));
        cache.Trim(4, 2);
        Assert.False(cache.TryGet(first, out _)); Assert.False(cache.TryGet(third, out _));
        cache.Set(first, "x"); cache.Trim(100, 0);
        Assert.False(cache.TryGet(first, out _));
    }

    [Fact]
    public async Task CacheTrimKeepsWarmTailAndReloadsEvictedImagesWithoutChangingPixels()
    {
        string root = Directory.CreateTempSubdirectory("vnotch-memory-test-").FullName;
        try
        {
            await using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            var entries = new List<ClipboardEntry>();
            var previews = new List<BitmapSource>();
            for (int i = 0; i < 5; i++)
            {
                entries.Add((await store.ImportAsync(new ClipboardCapture { ImagePng = Png(Image(50 + i, 40)) })).Entry!);
                previews.Add((await store.GetImagePreviewAsync(entries[^1].Id))!);
            }
            var cache = (IDictionary)typeof(ClipboardHistoryStore)
                .GetField("_imagePreviews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TrimMemoryCachesAsync(cancelled.Token));
            Assert.Equal(5, cache.Count);
            await store.TrimMemoryCachesAsync();
            Assert.Equal(3, cache.Count);
            Assert.Same(previews[^1], await store.GetImagePreviewAsync(entries[^1].Id));
            var reloaded = (await store.GetImagePreviewAsync(entries[0].Id))!;
            Assert.NotSame(previews[0], reloaded);
            Assert.Equal(Pixels(previews[0]), Pixels(reloaded));
            Assert.Equal(5, store.Snapshot().Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TrayOnlyTrimsCachesAfterHiddenExpiryAndCancelsOnReopen() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-tray-memory-").FullName;
        await using var store = new ClipboardHistoryStore(root);
        await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var cache = (ClipboardSearchTextCache)typeof(ClipboardHistoryStore)
                .GetField("_searchText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            cache.Set(first, new string('a', 256 * 1024));
            cache.Set(second, new string('b', 256 * 1024));
            tray.Attach(controller);
            window.Show();
            var timer = (UiScheduledTimer)typeof(ClipboardTray)
                .GetField("_cacheReleaseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            Assert.False(timer.IsEnabled);
            tray.Visibility = Visibility.Collapsed;
            Assert.True(timer.IsEnabled);
            Assert.Equal(TimeSpan.FromSeconds(30), timer.Interval);
            tray.Visibility = Visibility.Visible;
            Assert.False(timer.IsEnabled);
            Assert.True(cache.TryGet(first, out _));
            tray.Visibility = Visibility.Collapsed;
            timer.Interval = TimeSpan.FromMilliseconds(1);
            var characters = typeof(ClipboardSearchTextCache).GetField("_characters", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await WpfFrameWaiter.UntilAsync(() => (int)characters.GetValue(cache)! <= 256 * 1024, "hidden cache expiry");
            Assert.False(timer.IsEnabled);
            Assert.True(cache.TryGet(first, out _));
            Assert.False(cache.TryGet(second, out _));
        }
        finally
        {
            window.Close();
            tray.Dispose();
            await controller.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    });

    private static BitmapSource Image(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4]; new Random(width).NextBytes(pixels);
        var image = BitmapSource.Create(width, height, 120, 144, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }

    private sealed class ChunkedInput(byte[] bytes, CancellationTokenSource? cancellation = null, int reportedExtraBytes = 0) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int LargestRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length + reportedExtraBytes;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            int read = _inner.Read(buffer[..Math.Min(buffer.Length, 4096)]);
            cancellation?.Cancel(); return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
