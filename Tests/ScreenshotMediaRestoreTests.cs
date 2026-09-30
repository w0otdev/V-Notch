using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ScreenshotMediaRestoreTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DismissalRestoresMediaImageAndSettlesInterruptedThumbnailAnimation(bool pendingUpdate)
        => SharedStaTestRunner.Run(() =>
    {
        if (Application.Current == null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
            app.Resources["SFProText"] = new FontFamily("Segoe UI");
            app.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        }
        string settingsPath = Path.Combine(Path.GetTempPath(), $"vnotch-screenshot-restore-{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        settingsService.Save(new NotchSettings { EnableSpotlight = false, AutoCheckUpdates = false, EnableWeather = false });
        var services = new ServiceCollection();
        var configure = typeof(App).GetMethod("ConfigureServices", PrivateInstance | BindingFlags.Static)!;
        configure.Invoke(configure.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(typeof(App)), [services]);
        services.AddSingleton<ISettingsService>(settingsService);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            void Set(string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
            void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args);
            var oldImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var newImage = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4);
            Set("_isMusicCompactMode", true);
            window.ThumbnailImage.Source = window.CompactThumbnail.Source = oldImage;
            if (pendingUpdate) Set("_pendingFlipThumbnail", newImage);
            else
            {
                Set("_isThumbnailSwitchActive", true);
                window.ThumbnailImageNext.Source = window.CompactThumbnailNext.Source = newImage;
                window.CompactThumbnailNext.Visibility = Visibility.Visible;
            }
            window.CompactThumbnail.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
            window.CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, TimeSpan.Zero));
            window.CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(6, TimeSpan.Zero));
            Invoke("EnsureScreenshotSurface");
            var arbiter = (CompactPillArbiter)typeof(MainWindow).GetField("_compactPillArbiter", PrivateInstance)!.GetValue(window)!;
            Set("_screenshotSlotToken", arbiter.TryAcquire(CompactPillSlot.Screenshot).Token);
            Invoke("FinishScreenshotDismissal", false);

            Assert.Same(newImage, window.CompactThumbnail.Source);
            Assert.Equal(1, window.CompactThumbnail.Opacity);
            Assert.Equal(1, window.CompactThumbnailOutScale.ScaleX);
            Assert.Equal(0, window.CompactThumbnailOutBlur.Radius);
            Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
            Assert.Equal(Visibility.Collapsed, window.CompactThumbnailNext.Visibility);
            Assert.Null(window.CompactThumbnailNext.Source);
            Assert.Null(typeof(MainWindow).GetField("_pendingFlipThumbnail", PrivateInstance)!.GetValue(window));
            Assert.Equal(CompactPillSlot.None, arbiter.ActiveSlot);
        }
        finally
        {
            window.Close();
            File.Delete(settingsPath);
        }
    });
}
