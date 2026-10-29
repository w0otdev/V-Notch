using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClockPageRenderingTests
{
    [Theory]
    [InlineData("digital", "en")]
    [InlineData("wordclock", "en")]
    [InlineData("wordclock", "vi")]
    [InlineData("wordclock", "de")]
    [InlineData("wordclock", "ru")]
    [InlineData("wordclock", "ja")]
    public void TextClocksRenderVisibleTimeInsideTheClockSlot(string style, string language) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture(language, greeting: false,
            configureSettings: settings => { settings.EnableLocalOnlyMode = true; settings.ClockPageStyle = style; },
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.ApplyClockPageStyle();
        TextBlock clock = style == "digital" ? window.ClockViewDigitalClock : window.ClockViewWordClock;
        var viewbox = Assert.IsType<Viewbox>(clock.Parent);
        var slot = Assert.IsType<Grid>(viewbox.Parent);

        // Render the production clock subtree independently of the collapsed tab.
        // A visibility-only check misses black text on the dark clock background.
        slot.Children.Remove(viewbox);
        var surface = new Border { Width = 170, Height = 170, Background = Brushes.Black, Child = viewbox };
        var time = new DateTime(2026, 10, 27, 23, 59, 0, DateTimeKind.Local);
        clock.Text = style == "digital" ? time.ToString("HH:mm") : WordClock.FormatLocalizedTime(time);
        surface.Measure(new Size(170, 170));
        surface.Arrange(new Rect(0, 0, 170, 170));
        surface.UpdateLayout();
        Assert.False(string.IsNullOrWhiteSpace(clock.Text));

        foreach (int dpi in new[] { 96, 144, 192 })
        {
            double scale = dpi / 96d;
            int size = (int)(170 * scale);
            var bitmap = new RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            int stride = size * 4;
            var pixels = new byte[stride * size];
            bitmap.CopyPixels(pixels, stride, 0);
            int ink = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (pixels[y * stride + x * 4] > 32)
                    {
                        ink++;
                        Assert.InRange(x / scale, 9, 161);
                        Assert.InRange(y / scale, 9, 161);
                    }
            Assert.True(ink > 100, $"{style} must render readable time in {language} at {dpi} DPI.");
        }

        surface.Child = null;
        slot.Children.Add(viewbox);
    });
}
