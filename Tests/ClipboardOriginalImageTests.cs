using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardOriginalImageTests
{
    [Fact]
    public void OriginalDecodeReadsAwayFromDispatcherAndDetachesFullResolutionPixels() => SharedStaTestRunner.RunAsync(async () =>
    {
        const int width = 257, height = 193;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i % 251);
            pixels[i + 1] = 90;
            pixels[i + 2] = 180;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        var bytes = encoded.ToArray();
        using var stream = new TrackingStream(bytes);
        int uiThread = Environment.CurrentManagedThreadId;
        var image = await ClipboardOriginalImage.DecodeAsync(stream);
        Assert.NotEmpty(stream.ReadThreads);
        Assert.DoesNotContain(uiThread, stream.ReadThreads);
        Assert.True(image.IsFrozen);
        Assert.Equal(width, image.PixelWidth);
        Assert.Equal(height, image.PixelHeight);
        stream.Dispose();
        Array.Clear(bytes);
        var decoded = new byte[pixels.Length];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(decoded, width * 4, 0);
        Assert.Equal(pixels, decoded);
    });

    [Fact]
    public async Task CancelledDecodeDoesNotReadTheOriginal()
    {
        using var stream = new TrackingStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipboardOriginalImage.DecodeAsync(stream, cancellation.Token));
        Assert.Empty(stream.ReadThreads);
    }

    [Fact]
    public void LockingPersonalDuringDecodeRejectsCopyAndWipesOwnedPngBytes() => SharedStaTestRunner.RunAsync(async () =>
    {
        string directory = Directory.CreateTempSubdirectory("vnotch-copy-decode-").FullName;
        using var store = new ClipboardHistoryStore(directory);
        byte[]? ownedBytes = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store, bytes =>
        {
            ownedBytes = bytes;
            started.SetResult();
            return finish.Task;
        });
        try
        {
            await store.InitializeAsync();
            using var pin = new System.Security.SecureString();
            foreach (char c in "123456") pin.AppendChar(c);
            pin.MakeReadOnly();
            Assert.True(await store.UnlockPersonalAsync(pin, true));
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var png = new MemoryStream();
            encoder.Save(png);
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = png.ToArray(), TargetCategory = "Personal" })).Entry!;
            Task copy = controller.CopyAsync(entry);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(ownedBytes);
            Assert.Contains(ownedBytes, value => value != 0);
            await store.LockPersonalAsync();
            finish.SetResult(image);
            await Assert.ThrowsAsync<InvalidOperationException>(() => copy);
            Assert.All(ownedBytes, value => Assert.Equal(0, value));
        }
        finally
        {
            finish.TrySetCanceled();
            await controller.DisposeAsync();
            await store.DisposeAsync();
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, true);
        }
    });

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal readonly HashSet<int> ReadThreads = [];
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadThreads.Add(Environment.CurrentManagedThreadId);
            return base.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            ReadThreads.Add(Environment.CurrentManagedThreadId);
            return base.Read(buffer);
        }
    }
}
