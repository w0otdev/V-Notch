using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TextRenderingTests
{
    [Fact]
    public void IndependentWindowAndPopupTreesInheritRenderingDefaults() => SharedStaTestRunner.Run(() =>
    {
        var run = new Run("Tiếng Việt");
        var paragraph = new Paragraph(run);
        var document = new FlowDocument(paragraph);
        var controls = new FrameworkElement[]
        {
            new TextBlock(), new TextBox(), new RichTextBox(document), new PasswordBox(),
            new Button(), new ContentPresenter(), new AccessText(),
            new TextBlock { Style = new Style(typeof(TextBlock)) }
        };
        var panel = new StackPanel();
        foreach (var control in controls) panel.Children.Add(control);
        var window = new BackgroundWindow();
        var menu = new ContextMenu();
        var tooltip = new ToolTip();
        var popup = new Popup();
        foreach (FrameworkElement root in new FrameworkElement[] { window, menu, tooltip, popup })
        {
            switch (root)
            {
                case ContextMenu contextMenu: contextMenu.Items.Add(panel); break;
                case Popup popupRoot: popupRoot.Child = panel; break;
                case ContentControl contentRoot: contentRoot.Content = panel; break;
            }
            root.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            // Hinting inherits through the compositor's visual tree, rather than
            // through the dependency-property values returned on each child.
            Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(root));
            foreach (DependencyObject element in controls.Cast<DependencyObject>().Concat(new DependencyObject[] { document, paragraph, run }))
            {
                Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(element));
                Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(element));
            }
            switch (root)
            {
                case ContextMenu contextMenu: contextMenu.Items.Clear(); break;
                case Popup popupRoot: popupRoot.Child = null; break;
                case ContentControl contentRoot: contentRoot.Content = null; break;
            }
        }
        window.Close();

        // Preserve the opaque setup pages' explicit ClearType setting.
        var child = new TextBlock();
        tooltip.Content = child;
        TextOptions.SetTextRenderingMode(tooltip, TextRenderingMode.ClearType);
        tooltip.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(child));
        tooltip.Content = null;
    });

    [Fact]
    public void TextRenderersApplyThePolicyWithoutAPlacementWindow() => SharedStaTestRunner.Run(() =>
    {
        foreach (FrameworkElement renderer in new FrameworkElement[]
        {
            new TextBlock(), new TextBox(), new RichTextBox(), new PasswordBox(),
            new ContentPresenter(), new AccessText(),
            new TextBlock { Style = new Style(typeof(TextBlock)) }
        })
        {
            renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(renderer));
            Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(renderer));
            if (renderer is TextBlock or AccessText)
            {
                Assert.False(renderer.UseLayoutRounding);
                Assert.False(renderer.SnapsToDevicePixels);
            }
        }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    public void SmallVietnameseTextHasAntialiasedEdgesOnTransparentSurfaces(int dpi) => SharedStaTestRunner.Run(() =>
    {
        var label = new TextBlock
        {
            Text = "Cài đặt • Tiếng Việt • Độ sáng 100%",
            FontFamily = AppFonts.Get("Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(8)
        };
        var window = new BackgroundWindow { Content = label };
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        double scale = dpi / 96d;
        VisualTreeHelper.SetRootDpi(label, new DpiScale(scale, scale));
        label.Measure(new Size(300, 50));
        label.Arrange(new Rect(0, 0, 300, 50));
        label.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(300 * scale), (int)(50 * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(label);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int edgePixels = 0, solidPixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte alpha = pixels[offset + 3];
            if (alpha == 0) continue;
            Assert.Equal(pixels[offset], pixels[offset + 1]);
            Assert.Equal(pixels[offset], pixels[offset + 2]);
            if (alpha < 255) edgePixels++;
            else solidPixels++;
        }
        Assert.True(solidPixels > 20, "Small text must retain visible strokes.");
        Assert.True(edgePixels > 20, "Glyph edges must have antialiasing coverage on transparent surfaces.");
        window.Content = null;
        window.Close();
    });

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void SmallLabelsUseSoftenedEdgesAtCommonDpi(int dpi) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("vi", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true);
        foreach (string styleName in new[] { "TitleText", "SubtitleText", "SmallText" })
        {
            var style = (Style)fixture.Window.FindResource(styleName);
            var label = new TextBlock { Style = style, Text = "Cài đặt • Độ sáng 100%", Margin = new Thickness(8) };
            Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(label));
            double softContrast = RenderEdgeContrast(label, dpi);
            // Verify that the production style applies the smoothing filter, using
            // identical glyphs and layout for the unfiltered reference.
            TextOptions.SetTextHintingMode(label, TextHintingMode.Fixed);
            double fixedContrast = RenderEdgeContrast(label, dpi);
            Assert.True(softContrast < fixedContrast,
                $"{styleName} at {dpi} DPI: filtered edge contrast {softContrast:F2}, fixed {fixedContrast:F2}.");
            Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(fixture.Window.MediaPlayback.TrackTitle));
            Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(fixture.Window.MediaPlayback.TrackArtist));
            Assert.Equal(TextHintingMode.Animated, TextOptions.GetTextHintingMode(fixture.Window.CompactTitleMarquee));
        }
    });

    private static double RenderEdgeContrast(TextBlock label, int dpi)
    {
        const int width = 300, height = 50;
        var surface = new Border { Width = width, Height = height, Background = Brushes.Black, Child = label };
        double scale = dpi / 96d;
        VisualTreeHelper.SetRootDpi(surface, new DpiScale(scale, scale));
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        long ink = 0, energy = 0;
        for (int y = 0; y < bitmap.PixelHeight - 1; y++)
            for (int x = 0; x < bitmap.PixelWidth - 1; x++)
            {
                int offset = y * stride + x * 4;
                int value = pixels[offset];
                int horizontal = pixels[offset + 4] - value;
                int vertical = pixels[offset + stride] - value;
                ink += value;
                energy += horizontal * horizontal + vertical * vertical;
            }
        surface.Child = null;
        Assert.True(ink > 0, "The label must render visible glyphs.");
        return energy / (double)ink;
    }
}
