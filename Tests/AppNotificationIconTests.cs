using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationIconTests
{
    [Fact]
    public void IconNormalizationPreservesOpaqueEdgesAndDoesNotFreezeTheCallerBitmap() => SharedStaTestRunner.Run(() =>
    {
        byte[] pixels = new byte[24 * 16 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 41; pixels[i + 1] = 113; pixels[i + 2] = 219; pixels[i + 3] = 255;
        }
        var source = new WriteableBitmap(BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 24 * 4));
        var normalized = Assert.IsAssignableFrom<BitmapSource>(AppNotificationService.NormalizeAppIcon(source));
        Assert.False(source.IsFrozen);
        Assert.True(normalized.IsFrozen);
        Assert.Equal(24, normalized.PixelWidth);
        Assert.Equal(16, normalized.PixelHeight);
        var actual = Task.Run(() =>
        {
            byte[] result = new byte[pixels.Length];
            normalized.CopyPixels(result, 24 * 4, 0);
            return result;
        }).GetAwaiter().GetResult();
        Assert.Equal(pixels, actual);
    });

    [Fact]
    public void EmptyLogoAllowsTheFallbackIconToBeUsed() => SharedStaTestRunner.Run(() =>
    {
        var empty = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4);
        Assert.Null(AppNotificationService.NormalizeAppIcon(empty));
        Assert.Null(AppNotificationService.NormalizeAppIcon(null));
    });

    [Fact]
    public void RegisteredImageIconUsesTheImagePixelsInsteadOfTheFileTypeIcon() => SharedStaTestRunner.Run(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"vnotch-app-icon-{Guid.NewGuid():N}.png");
        try
        {
            byte[] pixels = new byte[16 * 16 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 41; pixels[i + 1] = 113; pixels[i + 2] = 219; pixels[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);

            var icon = Assert.IsAssignableFrom<BitmapSource>(AppNotificationService.TryResolveAppIcon(path, "Test image icon"));
            var converted = new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
            byte[] center = new byte[4];
            converted.CopyPixels(new Int32Rect(converted.PixelWidth / 2, converted.PixelHeight / 2, 1, 1), center, 4, 0);
            Assert.Equal(new byte[] { 41, 113, 219, 255 }, center);
            Assert.True(icon.IsFrozen);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void NotificationLogoWithTilePaddingRemainsReadableInTheBanner() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture(
            "en", greeting: false,
            configureSettings: settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.ShowSystemNotifications = true;
            },
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var pixels = new byte[48 * 48 * 4];
        for (int y = 18; y < 30; y++)
            for (int x = 18; x < 30; x++)
            {
                int offset = (y * 48 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = 255;
            }
        var logo = BitmapSource.Create(48, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4);
        logo.Freeze();
        fixture.Window.ShowAppNotification(new AppNotificationInfo
        {
            AppName = "Test padded logo",
            Title = "New notification",
            AppIcon = logo
        });
        var displayed = Assert.IsAssignableFrom<BitmapSource>(fixture.Window.AppNotificationIconImage.Source);
        var converted = new FormatConvertedBitmap(displayed, PixelFormats.Bgra32, null, 0);
        var actual = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(actual, converted.PixelWidth * 4, 0);
        int visible = 0;
        for (int i = 3; i < actual.Length; i += 4)
            if (actual[i] > 8) visible++;
        Assert.True(visible >= converted.PixelWidth * converted.PixelHeight / 2,
            $"Only {visible} of {converted.PixelWidth * converted.PixelHeight} pixels contain the app logo.");
        Assert.True(displayed.IsFrozen);
        Assert.Equal(Visibility.Visible, fixture.Window.AppNotificationIconImage.Visibility);
    });
}
