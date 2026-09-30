using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ScreenshotTrayTests
{
    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 12, 34, 56, 255, 78, 90, 123, 255 }, 8);
        image.Freeze();
        return image;
    }

    [Fact]
    public void SuccessfulKeepDismissesOnlyAfterBusyStateIsReleased() => SharedStaTestRunner.Run(() =>
    {
        var tray = new ScreenshotTray();
        tray.Present(Image());
        tray.KeepRequested = _ =>
        {
            Assert.True(tray.IsBusy);
            return Task.FromResult(true);
        };
        int dismissals = 0;
        tray.DismissRequested += () =>
        {
            Assert.False(tray.IsBusy);
            Assert.True(((Button)tray.FindName("KeepButton")).IsEnabled);
            Assert.True(((Button)tray.FindName("DismissButton")).IsEnabled);
            dismissals++;
        };
        ((Button)tray.FindName("KeepButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, dismissals);
    });

    [Fact]
    public void PendingKeepRejectsDoubleClicksAndCannotDismissAReopenedPreview() => SharedStaTestRunner.Run(() =>
    {
        var tray = new ScreenshotTray();
        var image = Image();
        tray.Present(image);
        var completion = new TaskCompletionSource<bool>();
        int keeps = 0, dismissals = 0;
        tray.KeepRequested = _ => { keeps++; return completion.Task; };
        tray.DismissRequested += () => dismissals++;
        var button = (Button)tray.FindName("KeepButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(tray.IsBusy);
        Assert.Equal(1, keeps);
        tray.Clear();
        tray.Present(image); // The same bitmap can belong to a new presentation.
        completion.SetResult(true);
        var frame = new System.Windows.Threading.DispatcherFrame();
        tray.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert.False(tray.IsBusy);
        Assert.Equal(0, dismissals);
        Assert.Same(image, ((Image)tray.FindName("PreviewImage")).Source);
    });

    [Fact]
    public void FailedKeepAllowsRetryAndThenDismisses() => SharedStaTestRunner.Run(() =>
    {
        var tray = new ScreenshotTray();
        tray.Present(Image());
        int attempts = 0, dismissals = 0;
        tray.KeepRequested = _ => ++attempts == 1
            ? Task.FromException<bool>(new IOException("Export failed"))
            : Task.FromResult(true);
        tray.DismissRequested += () => dismissals++;
        var button = (Button)tray.FindName("KeepButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(tray.IsBusy);
        Assert.Equal(0, dismissals);
        Assert.Equal(Loc.Get("screenshot.error"), ((TextBlock)tray.FindName("HintText")).Text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(tray.IsBusy);
        Assert.Equal(1, dismissals);
    });

    [Fact]
    public void FullShelfKeepsThePreviewAvailable() => SharedStaTestRunner.Run(() =>
    {
        var tray = new ScreenshotTray();
        var image = Image();
        tray.Present(image);
        tray.KeepRequested = _ => Task.FromResult(false);
        tray.DismissRequested += () => Assert.Fail("A failed keep must remain available for retry.");
        ((Button)tray.FindName("KeepButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(tray.IsBusy);
        Assert.Same(image, ((Image)tray.FindName("PreviewImage")).Source);
        Assert.Equal(Loc.Get("screenshot.full"), ((TextBlock)tray.FindName("HintText")).Text);
    });

    [Fact]
    public void CancelledDragRestoresOpacityAndReleasesBusyBeforeNotifyingHost() => SharedStaTestRunner.Run(() =>
    {
        var tray = new ScreenshotTray();
        tray.Present(Image());
        var source = (Image)tray.FindName("PreviewImage");
        source.Opacity = 0.8;
        int cancellations = 0;
        tray.DragCancelled += () =>
        {
            Assert.False(tray.IsBusy);
            Assert.False(tray.IsDragging);
            Assert.Equal(0.8, source.Opacity);
            cancellations++;
        };
        // An invisible source cancels before entering the native drag loop.
        var task = tray.DragImageAsync(source);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => frame.Continue = false;
        task.GetAwaiter().OnCompleted(() => frame.Continue = false);
        timeout.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timeout.Stop();
        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
        Assert.Equal(1, cancellations);
    });

    [Fact]
    public void DeduplicationRemembersInterleavedImagesAndLateFolderPublications()
    {
        var cache = new ScreenshotContentDeduplicator();
        var now = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(cache.TryAccept("A", now));
        Assert.True(cache.TryAccept("B", now.AddSeconds(1)));
        Assert.False(cache.TryAccept("A", now.AddSeconds(12)));
        Assert.False(cache.TryAccept("B", now.AddSeconds(20)));
        Assert.True(cache.TryAccept("A", now.AddSeconds(43)));
    }

    [Fact]
    public void DeduplicationHistoryIsBounded()
    {
        var cache = new ScreenshotContentDeduplicator();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 33; i++) Assert.True(cache.TryAccept(i.ToString(), now.AddMilliseconds(i)));
        Assert.False(cache.TryAccept("32", now.AddMilliseconds(34)));
        Assert.True(cache.TryAccept("0", now.AddMilliseconds(35)));
    }

    [Fact]
    public void FingerprintIgnoresDpiAndPixelEncodingButDistinguishesContent() => SharedStaTestRunner.Run(() =>
    {
        string original = ScreenshotContentDeduplicator.Fingerprint(Image());
        var equivalent = BitmapSource.Create(2, 1, 144, 144, PixelFormats.Bgr24, null,
            new byte[] { 12, 34, 56, 78, 90, 123 }, 6);
        Assert.Equal(original, ScreenshotContentDeduplicator.Fingerprint(equivalent));
        var changed = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 13, 34, 56, 255, 78, 90, 123, 255 }, 8);
        Assert.NotEqual(original, ScreenshotContentDeduplicator.Fingerprint(changed));
        var reshaped = BitmapSource.Create(1, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 12, 34, 56, 255, 78, 90, 123, 255 }, 4);
        Assert.NotEqual(original, ScreenshotContentDeduplicator.Fingerprint(reshaped));
    });

    [Theory]
    [InlineData("SnippingTool", true)]
    [InlineData("ScreenClippingHost", true)]
    [InlineData("ShareX", true)]
    [InlineData("chrome", false)]
    [InlineData("explorer", false)]
    [InlineData("V-Notch", false)]
    [InlineData(null, false)]
    public void OnlyCaptureOwnersTriggerTheTray(string? owner, bool expected) =>
        Assert.Equal(expected, ScreenshotCaptureService.IsCaptureProcess(owner));

    [Fact]
    public void ScreenshotSlotPreemptsOtherCompactContentAndRejectsLowerPriorityFeedback()
    {
        var arbiter = new VNotch.Controllers.CompactPillArbiter();
        var previous = arbiter.TryAcquire(VNotch.Controllers.CompactPillSlot.Charging);
        var screenshot = arbiter.TryAcquire(VNotch.Controllers.CompactPillSlot.Screenshot);
        Assert.True(screenshot.Won);
        Assert.False(arbiter.IsTokenCurrent(previous.Token));
        Assert.False(arbiter.TryAcquire(VNotch.Controllers.CompactPillSlot.Volume).Won);
        arbiter.Release(screenshot.Token);
        Assert.True(arbiter.CanRestoreMedia(arbiter.Revision));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(6000, 600)]
    [InlineData(200, 4000)]
    [InlineData(64, 64)]
    public void InlinePreviewUsesTheSameViewportForEveryImage(int width, int height) => SharedStaTestRunner.Run(() =>
    {
        var tray = new ScreenshotTray();
        tray.Present(Image());
        var baseline = tray.ConfigureInline();
        var capture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null,
            new byte[width * height], width);
        capture.Freeze();
        tray.Present(capture);
        var size = tray.ConfigureInline();
        Assert.Equal(baseline, size);
        Assert.Equal(304, size.Width);
        Assert.Equal(136, ((System.Windows.Controls.Border)tray.FindName("PreviewFrame")).Height);
        Assert.Equal(Stretch.Uniform, ((Image)tray.FindName("PreviewImage")).Stretch);
    });

    [Fact]
    public void OwnerlessAndUnknownToolsCanPublishImages() => SharedStaTestRunner.Run(() =>
    {
        foreach (string? owner in new string?[] { null, "explorer", "custom-capture-tool" })
        {
            int captures = 0;
            using var service = new ScreenshotCaptureService(() => owner, () => 42, Image);
            service.Captured += _ => captures++;
            service.NotifyClipboardUpdated();
            service.ReadPendingClipboard();
            Assert.Equal(1, captures);
        }
    });

    [Theory]
    [InlineData("capture.PNG", true)]
    [InlineData("capture.jpg", true)]
    [InlineData("capture.bmp", true)]
    [InlineData("capture.mp4", false)]
    [InlineData("capture.tmp", false)]
    public void FolderCaptureFiltersOutVideoAndPartialFiles(string path, bool expected) =>
        Assert.Equal(expected, ScreenshotFolderService.IsImagePath(path));

    [Fact]
    public void BusyClipboardRetriesAndPublishesEachSequenceOnce() => SharedStaTestRunner.Run(() =>
    {
        int reads = 0, captures = 0;
        using var service = new ScreenshotCaptureService(() => "SnippingTool", () => 10, () =>
        {
            if (++reads == 1) throw new ExternalException("Clipboard busy");
            return Image();
        });
        service.Captured += image => { Assert.True(image.IsFrozen); captures++; };
        Assert.False(service.NotifyClipboardUpdated());
        service.ReadPendingClipboard();
        Assert.Equal(0, captures);
        service.ReadPendingClipboard();
        Assert.Equal(1, captures);
        service.NotifyClipboardUpdated();
        service.ReadPendingClipboard();
        Assert.Equal(1, captures);
        Assert.Equal(2, reads);
    });

    [Fact]
    public void ClipboardReplacementCancelsPendingScreenshotWithoutReadingNewContent() => SharedStaTestRunner.Run(() =>
    {
        uint sequence = 1;
        int reads = 0;
        using var service = new ScreenshotCaptureService(() => "SnippingTool", () => sequence,
            () => { reads++; return Image(); });
        service.NotifyClipboardUpdated();
        sequence++;
        service.ReadPendingClipboard();
        Assert.Equal(0, reads);
    });

    [Fact]
    public void RetriesAreBoundedAndDisposalCancelsCapture() => SharedStaTestRunner.Run(() =>
    {
        int reads = 0;
        var service = new ScreenshotCaptureService(() => "SnippingTool", () => 1,
            () => { reads++; return null; });
        service.NotifyClipboardUpdated();
        for (int i = 0; i < 20; i++) service.ReadPendingClipboard();
        Assert.Equal(5, reads);
        service.NotifyClipboardUpdated();
        service.Dispose();
        service.ReadPendingClipboard();
        Assert.Equal(5, reads);
        Assert.False(service.NotifyClipboardUpdated());
    });

    [Fact]
    public void PngExportPreservesPixelsAndCleanupNeverDeletesKeptImages()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-screenshot-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ScreenshotFileStore(root);
            string saved = store.Save(Image(), keep: true);
            string expired = store.Save(Image(), keep: false);
            string recent = store.Save(Image(), keep: false);
            File.SetLastWriteTimeUtc(saved, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-2));
            using (var stream = File.OpenRead(saved))
            {
                var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal(2, decoded.PixelWidth);
                Assert.Equal(1, decoded.PixelHeight);
                var pixels = new byte[8];
                decoded.CopyPixels(pixels, 8, 0);
                Assert.Equal(new byte[] { 12, 34, 56, 255, 78, 90, 123, 255 }, pixels);
            }
            store.CleanExpiredExports();
            Assert.True(File.Exists(saved));
            Assert.True(File.Exists(recent));
            Assert.False(File.Exists(expired));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TrayRendersAtNativeSizeAndClearsItsImage() => SharedStaTestRunner.Run(() =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        string previousLanguage = Loc.CurrentLanguage;
        try
        {
            AnimationConfig.SetReduceMotion(true);
            var tray = new ScreenshotTray();
            tray.Resources["SFProText"] = new FontFamily("pack://application:,,,/V-Notch;component/Fonts/#SF Pro Text, Segoe UI");
            BitmapSource source = Image();
            string? input = Environment.GetEnvironmentVariable("VNOTCH_SCREENSHOT_PREVIEW_INPUT");
            if (!string.IsNullOrEmpty(input))
            {
                using var stream = File.OpenRead(input);
                source = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                source.Freeze();
            }
            foreach (var language in new[] { "en", "vi", "de", "fr", "hi", "ar", "ja" })
            {
                Loc.SetLanguage(language);
                tray.Present(source);
                tray.Measure(new Size(304, double.PositiveInfinity));
                tray.Arrange(new Rect(tray.DesiredSize));
                tray.UpdateLayout();
                Assert.Equal(304, tray.ActualWidth);
                Assert.InRange(tray.ActualHeight, 240, 350);
                Assert.Same(source, ((Image)tray.FindName("PreviewImage")).Source);
            }
            string? output = Environment.GetEnvironmentVariable("VNOTCH_SCREENSHOT_PREVIEW_OUTPUT");
            if (!string.IsNullOrEmpty(output))
            {
                Loc.SetLanguage("vi");
                tray.Present(source);
                tray.Measure(new Size(304, double.PositiveInfinity));
                tray.Arrange(new Rect(tray.DesiredSize));
                tray.UpdateLayout();
                var bitmap = new RenderTargetBitmap(608, (int)Math.Ceiling(tray.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(tray);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(output);
                encoder.Save(stream);
            }
            tray.Clear();
            Assert.Null(((Image)tray.FindName("PreviewImage")).Source);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(previousMotion);
            Loc.SetLanguage(previousLanguage);
        }
    });

    [Fact]
    public void ScreenshotPreferenceSurvivesCloningAndIsDetectedAsAChange()
    {
        var original = new NotchSettings();
        Assert.True(original.EnableScreenshotTray);
        var changed = original.Clone();
        changed.EnableScreenshotTray = false;
        Assert.False(original.ValueEquals(changed));
        Assert.False(changed.Clone().EnableScreenshotTray);
    }
}
