using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationTypographyTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void NotificationReservesTheFullHeightOfVietnameseTextAndDescenders(int dpi) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("vi", greeting: false,
            configureSettings: settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.ShowSystemNotifications = true;
                settings.AutoDismissNotifications = false;
            }, configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        window.ShowAppNotification(new AppNotificationInfo
        {
            AppName = "Ứng dụng ChatGPT", Title = "Cải thiện ảnh: Đặng Thị Quỳnh gypq",
            Body = "Người phụ trách đang phản hồi.\nỨng dụng đã xử lý xong ảnh.\nHãy kiểm tra kết quả gypq."
        });
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBanner.Opacity == 1 &&
            !System.Windows.DependencyPropertyHelper.GetValueSource(window.NotchBorder, FrameworkElement.HeightProperty).IsAnimated,
            "notification text layout settles", ct);
        var size = new Size(window.NotchBorder.Width, window.NotchBorder.Height);
        window.NotchBorder.Measure(size);
        window.NotchBorder.Arrange(new Rect(size));
        window.NotchBorder.UpdateLayout();
        Assert.Equal(Visibility.Visible, window.AppNotificationTitleText.Visibility);
        Assert.Equal(Visibility.Visible, window.AppNotificationBodyText.Visibility);
        foreach (var label in new[] { window.AppNotificationAppNameText, window.AppNotificationTitleText, window.AppNotificationBodyText }
                     .Where(label => label.Visibility == Visibility.Visible))
        {
            Assert.Equal(FontWeights.Bold, label.FontWeight);
            Assert.Contains("SF Pro Display", label.FontFamily.Source);
            var reference = new TextBlock
            {
                Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize,
                FontWeight = label.FontWeight, FontStyle = label.FontStyle, FontStretch = label.FontStretch,
                TextWrapping = label.TextWrapping, TextTrimming = label.TextTrimming, Padding = label.Padding,
                LineHeight = label.LineHeight, LineStackingStrategy = label.LineStackingStrategy, MaxHeight = label.MaxHeight,
                Foreground = label.Foreground
            };
            TextOptions.SetTextFormattingMode(reference, TextOptions.GetTextFormattingMode(label));
            TextOptions.SetTextHintingMode(reference, TextOptions.GetTextHintingMode(label));
            reference.Measure(new Size(label.ActualWidth, double.PositiveInfinity));
            Assert.True(label.ActualHeight >= reference.DesiredSize.Height - .1,
                $"{label.Name}: {label.ActualHeight:F2} DIP allocated, but complete glyphs need {reference.DesiredSize.Height:F2} DIP.");
            var rect = label.TransformToAncestor(window.AppNotificationBanner).TransformBounds(new Rect(label.RenderSize));
            Assert.True(rect.Bottom <= window.AppNotificationBanner.ActualHeight + .1, $"{label.Name} extends below its notification.");
            reference.Arrange(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
            reference.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(label.ActualWidth * dpi / 96),
                (int)Math.Ceiling(label.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(reference);
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 8);
            for (int x = 0; x < bitmap.PixelWidth; x++)
                Assert.True(pixels[(bitmap.PixelHeight - 1) * stride + x * 4 + 3] <= 8,
                    $"{label.Name} has glyph ink touching its bottom crop at {dpi} DPI.");
        }
    });
}
