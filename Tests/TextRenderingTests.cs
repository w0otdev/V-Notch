using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class TextRenderingTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void StationaryLabelsRetainSharpEdgesAtCommonDpi(int dpi) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("vi", greeting: false,
            configureSettings: settings => settings.EnableLocalOnlyMode = true);
        foreach (string styleName in new[] { "TitleText", "SubtitleText", "SmallText" })
        {
            var style = (Style)fixture.Window.FindResource(styleName);
            var label = new TextBlock { Style = style, Text = "Cài đặt • Độ sáng 100%", Margin = new Thickness(8) };
            double sharpness = RenderEdgeContrast(label, dpi);
            // Compare the same glyphs and metrics with WPF's animation filter. This
            // catches blurry stationary text without relying on machine-specific fonts
            // or exact pixel snapshots, and also exercises Vietnamese diacritics.
            TextOptions.SetTextHintingMode(label, TextHintingMode.Animated);
            double animationSharpness = RenderEdgeContrast(label, dpi);
            Assert.True(sharpness > animationSharpness * 1.1,
                $"{styleName} at {dpi} DPI: stationary edge contrast {sharpness:F2}, animation-filtered {animationSharpness:F2}.");
        }
    });

    private static double RenderEdgeContrast(TextBlock label, int dpi)
    {
        const int width = 300, height = 50;
        var surface = new Border { Width = width, Height = height, Background = Brushes.Black, Child = label };
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        double scale = dpi / 96d;
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
