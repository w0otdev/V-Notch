using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardCardContrastTests
{
    public static IEnumerable<object[]> CardCases()
    {
        foreach (string state in new[] { "normal", "hover", "selected" })
        {
            yield return [ClipboardKind.Text, "Neutral clipboard text", "", -1, state];
            yield return [ClipboardKind.Code, "const value = 42;", "", -1, state];
            yield return [ClipboardKind.Link, "https://example.com", "", -1, state];
            foreach (string extension in new[] { ".xlsx", ".pdf", ".docx", ".zip" })
                yield return [ClipboardKind.File, "", extension, -1, state];
            foreach (int brightness in new[] { 0, 255 })
            {
                yield return [ClipboardKind.Image, "", "", brightness, state];
                yield return [ClipboardKind.Color, brightness == 255 ? "#FFFFFF" : "#000000", "", -1, state];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CardCases))]
    public void VisibleTextKeepsContrastAcrossComposedSurfacesAndInteractionRims(
        ClipboardKind kind, string preview, string extension, int brightness, string state) => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var model = new ClipboardCardViewModel(new ClipboardEntry
        {
            Kind = kind,
            Title = kind == ClipboardKind.File ? "Project notes" + extension : "Clipboard text",
            Preview = preview,
            PrimaryExtension = extension,
            FileCount = kind == ClipboardKind.File ? 1 : 0,
            SourceApp = "Chrome",
            ByteSize = 7
        }, false);
        if (kind == ClipboardKind.Image)
            model.Image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { (byte)brightness, (byte)brightness, (byte)brightness, 255 }, 4);
        var card = CreateCard(tray, model);
        SetRim(card, "HoverRim", state == "hover");
        SetRim(card, "SelectedRim", state == "selected");

        var text = RelevantText(card).Where(element => element.Visibility == Visibility.Visible &&
            element.ActualWidth > 0 && element.ActualHeight > 0 && !string.IsNullOrEmpty(element.Text)).ToArray();
        Assert.Contains(text, element => element.Name == "CardFooterSource");
        Assert.Contains(text, element => element.Name == "CardFooterSize");
        var foregrounds = text.ToDictionary(element => element,
            element => Assert.IsType<SolidColorBrush>(element.Foreground).Color);
        var readings = new List<string>();
        string caseId = $"{kind}{extension}-{brightness}{(kind == ClipboardKind.Color ? "-" + preview.TrimStart('#') : "")}-{state}";

        foreach (int dpi in new[] { 96, 192 })
        {
            SaveArtifact(Render(card, dpi), $"clipboard-contrast-{caseId}-{dpi}.png");
            // Keep each text element and its own background in the composition.
            // Hiding the whole TextBlock would remove the color-card backing too.
            foreach (var element in text) element.SetCurrentValue(TextBlock.ForegroundProperty, Brushes.Transparent);
            var baseline = Render(card, dpi);
            SaveArtifact(baseline, $"clipboard-contrast-baseline-{caseId}-{dpi}.png");
            byte[] pixels = Pixels(baseline);
            foreach (var element in text)
            {
                // Padding is a backing-only border, not a possible glyph area.
                // Its fractional outside edge may blend with a white swatch.
                var padding = element.Padding;
                Point origin = element.TranslatePoint(new Point(padding.Left, padding.Top), card);
                var bounds = new Rect(origin, new Size(
                    Math.Max(0, element.ActualWidth - padding.Left - padding.Right),
                    Math.Max(0, element.ActualHeight - padding.Top - padding.Bottom)));
                double minimum = MinimumTextContrast(pixels, baseline.PixelWidth, baseline.PixelHeight,
                    bounds, dpi / 96d, foregrounds[element]);
                double required = element.Name is "CardPreviewText" or "CardFooterSource" or "CardFooterSize" ? 7 : 4.5;
                readings.Add($"{element.Name} at {dpi} DPI: {minimum:F4}:1 (minimum {required}:1)");
                Assert.True(minimum >= required,
                    $"{kind}{extension}/{brightness}/{state}/{dpi}: {element.Name} ({bounds}) has {minimum:F2}:1 contrast; requires {required}:1.");
            }
            foreach (var element in text) element.SetCurrentValue(TextBlock.ForegroundProperty,
                new SolidColorBrush(foregrounds[element]));
        }
        string? artifactDirectory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllLines(Path.Combine(artifactDirectory, $"clipboard-contrast-{caseId}.txt"), readings);
        }
    });

    [Fact]
    public void NeutralCardRetainsVisibleDepthAndAnEdgeAgainstTheBlackTray() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var card = CreateCard(tray, new ClipboardCardViewModel(new ClipboardEntry
        { Kind = ClipboardKind.Text, Preview = "Neutral clipboard text", SourceApp = "Chrome", ByteSize = 7 }, false));
        foreach (var text in RelevantText(card)) text.SetCurrentValue(TextBlock.ForegroundProperty, Brushes.Transparent);
        var bitmap = Render(card, 192);
        byte[] pixels = Pixels(bitmap);
        Color top = PixelAt(pixels, bitmap.PixelWidth, 105 * 2, 8 * 2);
        Color middle = PixelAt(pixels, bitmap.PixelWidth, 105 * 2, 85 * 2);
        Color footer = PixelAt(pixels, bitmap.PixelWidth, 105 * 2, 170 * 2);
        Color edge = PixelAt(pixels, bitmap.PixelWidth, 1, 85 * 2);
        foreach (var color in new[] { top, middle, footer, edge })
        {
            Assert.Equal(color.R, color.G);
            Assert.Equal(color.G, color.B);
        }
        Assert.True(top.R > middle.R && middle.R > footer.R, "The card should retain its light-to-dark depth.");
        Assert.True(middle.R >= 16, "The body must remain visibly lifted from the black tray.");
        Assert.True(edge.R > middle.R, "The resting rim must remain visible beside the dark surface.");
    });

    private static Border CreateCard(ClipboardTray tray, ClipboardCardViewModel model)
    {
        var list = (ListBox)tray.FindName("Cards");
        // A ContentPresenter owns DataTemplate.Triggers. LoadContent alone
        // would omit the backing that makes a white color swatch readable.
        var presenter = new ContentPresenter { Content = model, ContentTemplate = list.ItemTemplate, Width = 210, Height = 180 };
        presenter.Measure(new Size(210, 180));
        presenter.Arrange(new Rect(0, 0, 210, 180));
        presenter.UpdateLayout();
        var card = Assert.IsType<Border>(VisualTreeHelper.GetChild(presenter, 0));
        card.Height = 180;
        card.Measure(new Size(210, 180));
        card.Arrange(new Rect(0, 0, 210, 180));
        card.UpdateLayout();
        Descendants(card).OfType<Border>().Single(element => element.Name == "CardActions").Visibility = Visibility.Collapsed;
        return card;
    }

    private static IEnumerable<TextBlock> RelevantText(DependencyObject card) => Descendants(card).OfType<TextBlock>()
        .Where(element => element.Name is "CardPreviewText" or "FileHeroName" or "FileTypeTagText" or "CardFooterSource" or "CardFooterSize");

    private static void SetRim(DependencyObject card, string name, bool visible)
    {
        var rim = Descendants(card).OfType<System.Windows.Shapes.Rectangle>().Single(element => element.Name == name);
        rim.BeginAnimation(UIElement.OpacityProperty, null);
        rim.Opacity = visible ? 1 : 0;
    }

    private static double MinimumTextContrast(byte[] pixels, int width, int height, Rect bounds, double scale, Color foreground)
    {
        double minimum = double.PositiveInfinity;
        int left = Math.Max(0, (int)Math.Ceiling(bounds.Left * scale));
        int right = Math.Min(width, (int)Math.Floor(bounds.Right * scale));
        int top = Math.Max(0, (int)Math.Ceiling(bounds.Top * scale));
        int bottom = Math.Min(height, (int)Math.Floor(bounds.Bottom * scale));
        Assert.True(right > left && bottom > top, "Text must occupy a measurable region of the card.");
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                Color background = PixelAt(pixels, width, x, y);
                double alpha = foreground.A / 255d;
                double ink = Luminance(
                    foreground.R * alpha + background.R * (1 - alpha),
                    foreground.G * alpha + background.G * (1 - alpha),
                    foreground.B * alpha + background.B * (1 - alpha));
                double surface = Luminance(background.R, background.G, background.B);
                minimum = Math.Min(minimum, (Math.Max(ink, surface) + 0.05) / (Math.Min(ink, surface) + 0.05));
            }
        return minimum;
    }

    private static double Luminance(double red, double green, double blue)
    {
        static double Linear(double channel)
        {
            channel /= 255;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
    }

    // Pbgra bytes are already composited over the black tray, including any
    // partially transparent swatches. No unpremultiplication is required.
    private static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        int index = (y * width + x) * 4;
        return Color.FromRgb(pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    private static RenderTargetBitmap Render(FrameworkElement card, int dpi)
    {
        // Off-tree DataTemplate visuals have no presentation source scheduling
        // their redraw. Rebuild text drawings before sampling changed ink.
        foreach (var text in Descendants(card).OfType<TextBlock>()) text.InvalidateVisual();
        card.UpdateLayout();
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)(card.ActualWidth * scale), (int)(card.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(card);
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
