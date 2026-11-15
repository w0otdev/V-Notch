using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ResourceUsageRegressionTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(32, 8192)]
    [InlineData(8192, 32)]
    [InlineData(256, 256)]
    [InlineData(16, 8)]
    public void DirectImageFallbackBoundsBothDimensionsAndReleasesItsFile(int width, int height) => SharedStaTestRunner.Run(() =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-image-budget-").FullName;
        try
        {
            string path = Path.Combine(root, "image.png");
            WriteImage(path, width, height);
            var image = Assert.IsAssignableFrom<BitmapSource>(typeof(FileIconProvider)
                .GetMethod("TryGetDirectImage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path]));
            long bytes = (long)image.PixelWidth * image.PixelHeight * ((image.Format.BitsPerPixel + 7) / 8);
            output.WriteLine($"{width}x{height} source -> {image.PixelWidth}x{image.PixelHeight} preview, {bytes} decoded pixel bytes");
            Assert.InRange(image.PixelWidth, 1, 48);
            Assert.InRange(image.PixelHeight, 1, 48);
            Assert.Equal(Math.Min(48, Math.Max(width, height)), Math.Max(image.PixelWidth, image.PixelHeight));
            Assert.True(image.IsFrozen);
            Assert.InRange(Math.Abs(image.PixelWidth - image.PixelHeight * ((double)width / height)), 0, Math.Max(1, (double)width / height));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void ShellThumbnailStaysWithinItsRequestedPixelBudget() => SharedStaTestRunner.Run(() =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-shell-budget-").FullName;
        try
        {
            string path = Path.Combine(root, "image.png");
            WriteImage(path, 1024, 2048);
            var image = Assert.IsAssignableFrom<BitmapSource>(FileIconProvider.GetFileIcon(path));
            output.WriteLine($"Shell preview: {image.PixelWidth}x{image.PixelHeight}");
            Assert.InRange(image.PixelWidth, 1, 64);
            Assert.InRange(image.PixelHeight, 1, 64);
            Assert.True(image.IsFrozen);
            Assert.Same(image, FileIconProvider.GetFileIcon(path));
        }
        finally { FileIconProvider.ClearCache(); Directory.Delete(root, true); }
    });

    [Fact]
    public void SystemMonitorStopsForHiddenViewsAndResumesWhenVisible() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousMotion = AnimationConfig.ReduceMotion;
        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
            settings =>
            {
                settings.EnableLocalOnlyMode = true;
                settings.DisableMouseLeaveAutoClose = true;
                settings.ExpandedWidget = "sysmon";
            }, services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        var window = fixture.Window;
        var module = Field<SystemMonitorModule>(window, "_systemMonitorModule");
        var coordinator = Field<NotchTransitionCoordinator>(window, "_transitionCoordinator");
        try
        {
            AnimationConfig.SetReduceMotion(true);
            Assert.False(module.IsRunning);
            window.Show();
            foreach (var target in new[] { NotchView.Media, NotchView.Secondary, NotchView.Media, NotchView.Compact, NotchView.Media })
            {
                Assert.True(coordinator.RequestView(target, "ResourceUsageRegression"));
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "navigation to " + target, ct);
                bool visible = window.SystemMonitorWidgetContent.IsVisible;
                output.WriteLine($"{target}: widget visible={visible}, sampler running={module.IsRunning}");
                Assert.Equal(target == NotchView.Media, visible);
                Assert.Equal(visible, module.IsRunning);
            }

            var settings = Field<NotchSettings>(window, "_settings");
            settings.ExpandedWidget = "clock";
            Invoke(window, "ApplyExpandedWidgetMode", false);
            Assert.False(module.IsRunning);
            settings.ExpandedWidget = "sysmon";
            Invoke(window, "ApplyExpandedWidgetMode", false);
            Assert.True(module.IsRunning);

            window.Hide();
            Assert.False(module.IsRunning);
            int samples = 0;
            module.StatsUpdated += (_, _) => samples++;
            await Task.Delay(1200, ct);
            Assert.Equal(0, samples);
            window.Show();
            Assert.True(module.IsRunning);
            Assert.True(samples > 0);

            // Debug is an independent consumer, even with the widget hidden.
            typeof(MainWindow).GetField("_isDebugModeEnabled", Private)!.SetValue(window, true);
            window.Hide();
            Assert.True(module.IsRunning);
            window.ToggleDebugMode(false);
            Assert.False(module.IsRunning);
        }
        finally { AnimationConfig.SetReduceMotion(previousMotion); }
    });

    private static void WriteImage(string path, int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 100;
            pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
