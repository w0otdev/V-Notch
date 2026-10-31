using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardCardRenderingTests
{
    [Theory]
    [InlineData(140, false)]
    [InlineData(140, true)]
    [InlineData(180, false)]
    [InlineData(180, true)]
    public void LongPreviewStaysInsideItsInsetAndLeavesTheFooterClear(double height, bool selected) => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var card = CreateCard(tray, new ClipboardEntry
        {
            Kind = ClipboardKind.Text,
            Preview = string.Join("\n", Enumerable.Repeat("Long clipboard text with words and a verylongunbrokenidentifier0123456789", 30)),
            SourceApp = "svchost",
            ByteSize = 12_500
        }, height);
        var preview = Descendants(card).OfType<TextBlock>().Single(element => element.Name == "CardPreviewText");
        var viewport = Descendants(card).OfType<Grid>().Single(element => element.Name == "CardPreviewViewport");
        // Render the settled production selection rim without relying on the
        // animation clock or requiring the card's containing window to be visible.
        var selectionRim = Descendants(card).OfType<System.Windows.Shapes.Rectangle>().Single(element => element.Name == "SelectedRim");
        selectionRim.BeginAnimation(UIElement.OpacityProperty, null);
        selectionRim.Opacity = selected ? 1 : 0;
        Descendants(card).OfType<Border>().Single(element => element.Name == "CardActions").Visibility = Visibility.Collapsed;
        var origin = viewport.TranslatePoint(new Point(), card);
        var previewBounds = new Rect(origin, viewport.RenderSize);
        var layoutDpi = VisualTreeHelper.GetDpi(card);
        double horizontalPixel = 1 / layoutDpi.DpiScaleX;
        double verticalPixel = 1 / layoutDpi.DpiScaleY;
        Assert.InRange(previewBounds.Left, 14 - horizontalPixel, 14 + horizontalPixel);
        Assert.True(previewBounds.Right <= 196 + horizontalPixel);
        Assert.True(previewBounds.Bottom <= height - 48 + verticalPixel);

        foreach (int dpi in new[] { 96, 144, 192 })
        {
            double scale = dpi / 96d;
            preview.Visibility = Visibility.Visible;
            card.UpdateLayout();
            var rendered = Render(card, dpi);
            SaveArtifact(rendered, $"clipboard-long-text-{height}-{(selected ? "selected" : "normal")}-{dpi}.png");
            preview.Visibility = Visibility.Hidden;
            card.UpdateLayout();
            var withoutPreview = Render(card, dpi);
            var actual = Pixels(rendered);
            var baseline = Pixels(withoutPreview);
            int changed = 0;
            for (int y = 0; y < rendered.PixelHeight; y++)
                for (int x = 0; x < rendered.PixelWidth; x++)
                {
                    int offset = (y * rendered.PixelWidth + x) * 4;
                    if (Math.Abs(actual[offset] - baseline[offset]) <= 3 &&
                        Math.Abs(actual[offset + 1] - baseline[offset + 1]) <= 3 &&
                        Math.Abs(actual[offset + 2] - baseline[offset + 2]) <= 3) continue;
                    changed++;
                    Assert.InRange(x / scale, previewBounds.Left - 1, previewBounds.Right + 1);
                    Assert.InRange(y / scale, previewBounds.Top - 1, previewBounds.Bottom + 1);
                }
            Assert.True(changed > 100, "The preview must render visible text rather than hiding overflowing content entirely.");
        }
    });

    [Fact]
    public void RoundedCardClipContainsImagesAndBlurBeforeDeferredSizeEvents() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var card = CreateCard(tray, new ClipboardEntry { Kind = ClipboardKind.Image, IsPinned = true }, 150);
        var model = (ClipboardCardViewModel)card.DataContext;
        model.Image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 80, 160, 255, 255 }, 4);
        var content = Descendants(card).OfType<Grid>().Single(element => element.Name == "CardContentGrid");
        content.Effect = new BlurEffect { Radius = 12 };

        foreach (double height in new[] { 150d, 210d })
        {
            card.Height = height;
            Layout(card, height);
            // The outer shell's event-based clip is intentionally unavailable.
            // The presentation must clip the final effect output on its first render.
            card.Clip = null;
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                var bitmap = Render(card, dpi);
                var pixels = Pixels(bitmap);
                foreach ((int x, int y) in new[] { (1, 1), (bitmap.PixelWidth - 2, 1), (1, bitmap.PixelHeight - 2), (bitmap.PixelWidth - 2, bitmap.PixelHeight - 2) })
                    Assert.Equal(0, pixels[(y * bitmap.PixelWidth + x) * 4 + 3]);
                Assert.True(pixels[((bitmap.PixelHeight / 2) * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4 + 3] > 240);
                SaveArtifact(bitmap, $"clipboard-rounded-card-{height}-{dpi}.png");
            }
        }
    });

    [Fact]
    public void CreateBitmapBounds4kScreenshotWidthAndFreezesResult() => SharedStaTestRunner.Run(() =>
    {
        int stride = (3840 + 7) / 8;
        var source = BitmapSource.Create(
            3840,
            2160,
            96,
            96,
            PixelFormats.Indexed1,
            BitmapPalettes.BlackAndWhite,
            new byte[stride * 2160],
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var bitmap = ClipboardTray.CreateBitmap(stream.ToArray());

        Assert.True(bitmap.PixelWidth <= 420);
        Assert.Equal(420, bitmap.PixelWidth);
        Assert.True(bitmap.PixelHeight <= 420);
        Assert.True(bitmap.IsFrozen);
    });

    private static Border CreateCard(ClipboardTray tray, ClipboardEntry entry, double height)
    {
        var list = (ListBox)tray.FindName("Cards");
        var card = Assert.IsType<Border>(list.ItemTemplate.LoadContent());
        card.DataContext = new ClipboardCardViewModel(entry, false);
        card.Height = height;
        Layout(card, height);
        return card;
    }

    private static void Layout(FrameworkElement card, double height)
    {
        card.Measure(new Size(210, height));
        card.Arrange(new Rect(0, 0, 210, height));
        card.UpdateLayout();
    }

    private static RenderTargetBitmap Render(FrameworkElement visual, int dpi)
    {
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)(visual.ActualWidth * scale), (int)(visual.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void SaveArtifact(BitmapSource bitmap, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
