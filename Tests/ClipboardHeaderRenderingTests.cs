using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardHeaderRenderingTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void HeaderActionsPaintOnTheSameCenterline(int dpi) => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var actions = (StackPanel)tray.FindName("ClipboardHeaderActions");
        var owner = (Grid)actions.Parent;
        owner.Children.Remove(actions);
        actions.Margin = new Thickness(0);
        var surface = new Border
        {
            Width = 112,
            Height = 44,
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromRgb(8, 8, 12)),
            Child = actions
        };
        try
        {
            surface.Measure(new Size(112, 44));
            surface.Arrange(new Rect(0, 0, 112, 44));
            surface.UpdateLayout();
            double scale = dpi / 96d;
            var bitmap = new RenderTargetBitmap((int)(112 * scale), (int)(44 * scale), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            foreach (string name in new[] { "AddLabelButton", "SelectAllButton", "EmptyTrayButton" })
            {
                var button = (Button)tray.FindName(name);
                var icon = Assert.IsType<TrayIcon>(button.Content);
                Assert.Equal(16, icon.Width);
                Assert.Equal(16, icon.Height);
                Point origin = button.TranslatePoint(new Point(), surface);
                int minX = bitmap.PixelWidth, minY = bitmap.PixelHeight, maxX = -1, maxY = -1, ink = 0;
                for (int y = (int)(origin.Y * scale); y < (int)((origin.Y + button.ActualHeight) * scale); y++)
                    for (int x = (int)(origin.X * scale); x < (int)((origin.X + button.ActualWidth) * scale); x++)
                    {
                        int i = y * stride + x * 4;
                        if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) < 50) continue;
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); ink++;
                    }
                Assert.True(ink > 15, $"{name} must render a visible action at {dpi} DPI.");
                double centerX = (minX + maxX + 1) / (2 * scale);
                double centerY = (minY + maxY + 1) / (2 * scale);
                Assert.InRange(centerX - origin.X - button.ActualWidth / 2, -1, 1);
                Assert.InRange(centerY - origin.Y - button.ActualHeight / 2, -1, 1);
            }

            string? artifacts = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(artifacts))
            {
                Directory.CreateDirectory(artifacts);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(artifacts, $"clipboard-header-{dpi}.png"));
                encoder.Save(file);
            }
        }
        finally
        {
            surface.Child = null;
            actions.ClearValue(FrameworkElement.MarginProperty);
            owner.Children.Add(actions);
        }
    });
}
