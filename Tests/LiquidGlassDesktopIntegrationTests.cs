using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using Xunit;
using static VNotch.Tests.LiquidGlassSpotlightTests;

namespace VNotch.Tests;

// These tests require a real compositor, GPU and desktop capture session.
// Desktop attributes keep them out of the default background test run.
[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class LiquidGlassDesktopIntegrationTests
{
    [DesktopFact]
    [Trait("Category", "DesktopIntegration")]
    public void GlassShader_RenderedPixelsMatchCapturedBackdrop_InRealMainWindow()
    {
        RunStaAsync(async cancellationToken =>
        {
            string settingsDirectory = Path.Combine(
                Path.GetTempPath(), $"vnotch-main-glass-test-{Guid.NewGuid():N}");
            string settingsPath = Path.Combine(settingsDirectory, "settings.json");
            ServiceProvider? provider = null;
            MainWindow? host = null;
            Window? backdrop = null;
            MagnifierCaptureSource? magnifier = null;
            IntPtr captureBuffer = IntPtr.Zero;

            try
            {
                _ = CreateApplicationResources();
                var settingsService = new SettingsService(settingsPath, _ => { });
                settingsService.Save(CreateMainWindowLiquidGlassSettings());

                var services = new ServiceCollection();
                ServiceConfigurator.ConfigureServices(services);
                services.AddSingleton<ISettingsService>(settingsService);
                provider = services.BuildServiceProvider();
                host = provider.GetRequiredService<MainWindow>();

                backdrop = new Window
                {
                    Left = host.Left - 300,
                    Top = host.Top,
                    Width = 1000,
                    Height = 500,
                    WindowStyle = WindowStyle.None,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = new SolidColorBrush(Color.FromRgb(32, 192, 128))
                };
                backdrop.Show();
                backdrop.UpdateLayout();
                await WpfFrameWaiter.NextAsync(cancellationToken);

                host.ShowActivated = false;
                host.Show();
                host.UpdateLayout();

                // Exercise expanded MainWindow production transition before verifying
                // capture, texture, and composed desktop pixel fidelity.
                host.SetDebugViewState("MediaExpanded");

                var controllerField = typeof(MainWindow).GetField(
                    "_liquidGlass", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await WpfFrameWaiter.UntilAsync(() => controllerField.GetValue(host) is LiquidGlassController controller &&
                    controller.HasPresentedFrame && host.NotchBorder.ActualHeight >= 140, "glass presentation or transition completion", cancellationToken);
                await WpfFrameWaiter.NextAsync(cancellationToken);

                var controller = Assert.IsType<LiquidGlassController>(controllerField.GetValue(host));
                var effect = Assert.IsType<LiquidGlassRefractionEffect>(host.GlassBackdropImage.Effect);
                var source = Assert.IsType<D3DImage>(host.GlassBackdropImage.Source);
                var screenPoint = host.NotchBorder.PointToScreen(new Point(
                    host.NotchBorder.ActualWidth * 0.5,
                    host.NotchBorder.ActualHeight * 0.5));

                captureBuffer = Marshal.AllocHGlobal(4);
                magnifier = MagnifierCaptureSource.AcquireShared(IntPtr.Zero);
                Assert.True(magnifier.IsReady, "MainWindow integration requires Magnifier capture.");
                int captureX = 0, captureY = 0;
                await WpfFrameWaiter.UntilAsync(() => magnifier.CaptureInto(
                    (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y),
                    1, 1, captureBuffer, out captureX, out captureY),
                    "the captured MainWindow backdrop pixel", cancellationToken);

                var d3dCopy = ImageSourceSnapshot.Capture(source, source.PixelWidth, source.PixelHeight);
                int textureX = Math.Clamp((int)Math.Round(effect.OffX + effect.NotchW * 0.5),
                    0, d3dCopy.PixelWidth - 1);
                int textureY = Math.Clamp((int)Math.Round(effect.OffY + effect.NotchH * 0.5),
                    0, d3dCopy.PixelHeight - 1);
                var texturePixel = new byte[4];
                d3dCopy.CopyPixels(new Int32Rect(textureX, textureY, 1, 1), texturePixel, 4, 0);

                byte[] renderedPixel = [];
                await WpfFrameWaiter.UntilAsync(() =>
                {
                    renderedPixel = ReadRenderedCenterPixel(host.NotchBorder);
                    return renderedPixel[3] > 0 && renderedPixel[1] >= 80;
                }, "the rendered MainWindow glass pixel", cancellationToken);
                int magnifierPixel = Marshal.ReadInt32(captureBuffer);
                double dpi = VisualTreeHelper.GetDpi(host).DpiScaleX;
                string diagnostics =
                    $"mag=B{magnifierPixel & 255},G{(magnifierPixel >> 8) & 255},R{(magnifierPixel >> 16) & 255} " +
                    $"at={captureX},{captureY}; d3d=B{texturePixel[0]},G{texturePixel[1]},R{texturePixel[2]} " +
                    $"at={textureX},{textureY}; rendered=B{renderedPixel[0]},G{renderedPixel[1]},R{renderedPixel[2]},A{renderedPixel[3]}; " +
                    $"captureOrigin={controller.LastPresentedCaptureOriginX},{controller.LastPresentedCaptureOriginY}; " +
                    $"off={effect.OffX:F2},{effect.OffY:F2}; src={effect.SrcW:F0}x{effect.SrcH:F0}; " +
                    $"imageActual={host.GlassBackdropImage.ActualWidth:F2}x{host.GlassBackdropImage.ActualHeight:F2}; " +
                    $"notchActual={host.NotchBorder.ActualWidth:F2}x{host.NotchBorder.ActualHeight:F2}; dpi={dpi:F3}";
                Console.WriteLine(diagnostics);

                Assert.True(((magnifierPixel >> 8) & 255) >= 150, diagnostics);
                Assert.True(texturePixel[1] >= 150, diagnostics);
                Assert.True(renderedPixel[3] > 0 && renderedPixel[1] >= 80, diagnostics);
            }
            finally
            {
                if (magnifier != null) MagnifierCaptureSource.ReleaseShared(IntPtr.Zero);
                if (captureBuffer != IntPtr.Zero) Marshal.FreeHGlobal(captureBuffer);
                host?.Close();
                provider?.Dispose();
                backdrop?.Close();
                if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, recursive: true);
            }
        });
    }

    [DesktopFact]
    [Trait("Category", "DesktopIntegration")]
    public void LiquidGlass_LiveDesktop_CaptureCoordinatesAndRapidReopenStayValid()
    {
        RunStaAsync(async cancellationToken =>
        {
            bool reduceMotion = AnimationConfig.ReduceMotion;
            var background = new Window
            {
                Left = SystemParameters.PrimaryScreenWidth / 2 - 450,
                Top = 0,
                Width = 900,
                Height = 500,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(32, 192, 128))
            };
            SpotlightWindowFixture? fixture = null;
            SpotlightWindow? window = null;
            MagnifierCaptureSource? capture = null;
            IntPtr pixels = Marshal.AllocHGlobal(40 * 40 * 4);
            try
            {
                background.Show();
                background.UpdateLayout();
                await WpfFrameWaiter.NextAsync(cancellationToken);
                capture = MagnifierCaptureSource.AcquireShared(IntPtr.Zero);
                Assert.True(capture.IsReady, "Desktop integration requires Magnifier capture.");
                await VerifyLiveDesktopBackdropCaptureAsync(capture, background, pixels, cancellationToken);

                AnimationConfig.SetReduceMotion(false);
                fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
                window = fixture.Window;
                window.MorphHostOverride = new GlassMorphHost();
                var controllerField = typeof(SpotlightWindow).GetField("_liquidGlass",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    window.ShowSpotlight();
                    var controller = (LiquidGlassController)controllerField.GetValue(window)!;
                    await WpfFrameWaiter.UntilAsync(() => controller.HasPresentedFrame, "glass presentation or transition completion", cancellationToken);
                    await VerifySpotlightSurfaceFramesAsync(window, controller, cycle, cancellationToken);

                    if (cycle == 0)
                    {
                        await VerifyLiveDesktopDynamicUpdateAsync(window, background, controller, cancellationToken);
                    }
                    if (cycle == 1)
                    {
                        typeof(SpotlightWindow).GetMethod("OnGpuRefractionFailure",
                            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                                new object[] { new InvalidOperationException("Injected GPU loss") });
                        await WpfFrameWaiter.UntilAsync(() => controller.HasPresentedFrame, "glass presentation or transition completion", cancellationToken);
                        Assert.IsType<WriteableBitmap>(window.GlassBackdropImage.Source);
                        Assert.Null(window.GlassBackdropImage.Effect);
                        Assert.True(double.IsNaN(window.GlassBackdropImage.Width));
                    }
                    // A theme switch stops and immediately restarts the controller
                    // while its native worker can still be inside CaptureInto.
                    window.ApplySettings(new NotchSettings { NotchStyle = "default" });
                    window.ApplySettings(new NotchSettings { NotchStyle = "liquidglass" });
                    await WpfFrameWaiter.UntilAsync(() => controller.HasPresentedFrame, "glass presentation or transition completion", cancellationToken);
                    window.HideSpotlight();
                    await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "glass presentation or transition completion", cancellationToken);
                    Assert.False(controller.HasPresentedFrame);
                }
            }
            finally
            {
                fixture?.Dispose();
                if (capture != null) MagnifierCaptureSource.ReleaseShared(IntPtr.Zero);
                Marshal.FreeHGlobal(pixels);
                background.Close();
                AnimationConfig.SetReduceMotion(reduceMotion);
            }
        });
    }

    private static async Task VerifyLiveDesktopBackdropCaptureAsync(MagnifierCaptureSource capture, Window background, IntPtr pixels, CancellationToken cancellationToken)
    {
        var origin = background.PointToScreen(new Point(80, 80));
        int pixel = 0;
        await WpfFrameWaiter.UntilAsync(() =>
        {
            if (!capture.CaptureInto((int)origin.X, (int)origin.Y, 40, 40, pixels,
                    out int actualX, out int actualY)) return false;
            Assert.Equal((int)origin.X, actualX);
            Assert.Equal((int)origin.Y, actualY);
            pixel = Marshal.ReadInt32(pixels, (20 * 40 + 20) * 4);
            return ((pixel >> 8) & 255) is >= 175 and <= 205;
        }, "the green desktop backdrop capture", cancellationToken);
        Assert.InRange((pixel >> 8) & 255, 175, 205);
        Assert.InRange((pixel >> 16) & 255, 20, 45);
        Assert.InRange(pixel & 255, 110, 145);
    }

    private static async Task VerifySpotlightSurfaceFramesAsync(SpotlightWindow window, LiquidGlassController controller, int cycle, CancellationToken cancellationToken)
    {
        var surface = window.GlassBackdropImage.Source;
        Assert.IsType<D3DImage>(surface);
        for (int frame = 0; frame < 20; frame++)
        {
            await WpfFrameWaiter.NextAsync(cancellationToken);
            Assert.Same(surface, window.GlassBackdropImage.Source);
            Assert.True(controller.HasPresentedFrame);
            var effect = Assert.IsType<LiquidGlassRefractionEffect>(window.GlassBackdropImage.Effect);
            double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert.True(effect.SrcW >= window.GlassBackdropHost.ActualWidth * dpi);
            Assert.True(effect.SrcH >= window.GlassBackdropHost.ActualHeight * dpi);
            var d3d = Assert.IsType<D3DImage>(surface);
            var copy = ImageSourceSnapshot.Capture(d3d, d3d.PixelWidth, d3d.PixelHeight);
            Assert.NotNull(copy);
            int sx = Math.Clamp((int)(effect.OffX + effect.NotchW / 2), 0, copy.PixelWidth - 1);
            int sy = Math.Clamp((int)(effect.OffY + effect.NotchH / 2), 0, copy.PixelHeight - 1);
            var sample = new byte[4];
            copy.CopyPixels(new Int32Rect(sx, sy, 1, 1), sample, 4, 0);
            Assert.True(sample[1] >= 175 && sample[1] <= 205,
                $"cycle={cycle} frame={frame} pixel={sample[2]},{sample[1]},{sample[0]} " +
                $"screen={controller.LastPresentedCaptureOriginX + sx},{controller.LastPresentedCaptureOriginY + sy} " +
                $"source={copy.PixelWidth}x{copy.PixelHeight} offset={effect.OffX},{effect.OffY}");
            Assert.InRange(sample[2], (byte)20, (byte)45);
            if (frame == 8)
            {
                window.HideSpotlight();
                window.ToggleFromHotkey();
                await WpfFrameWaiter.UntilAsync(() => controller.HasPresentedFrame,
                    "a frame after reopening Spotlight", cancellationToken);
            }
        }
    }

    private static async Task VerifyLiveDesktopDynamicUpdateAsync(SpotlightWindow window, Window background, LiquidGlassController controller, CancellationToken cancellationToken)
    {
        await WpfFrameWaiter.UntilAsync(() => double.IsNaN(window.Shell.Height), "glass presentation or transition completion", cancellationToken);
        var effect = Assert.IsType<LiquidGlassRefractionEffect>(window.GlassBackdropImage.Effect);
        int physicalX = controller.LastPresentedCaptureOriginX + (int)(effect.OffX + effect.NotchW / 2);
        int physicalY = controller.LastPresentedCaptureOriginY + (int)(effect.OffY + effect.NotchH / 2);
        var panel = new Canvas();
        var marker = new Border { Width = 4, Height = 4, Background = Brushes.Red };
        var bgOrigin = background.PointToScreen(new Point());
        double dpi = VisualTreeHelper.GetDpi(background).DpiScaleX;
        Canvas.SetLeft(marker, (physicalX - bgOrigin.X) / dpi - 1);
        Canvas.SetTop(marker, (physicalY - bgOrigin.Y) / dpi - 1);
        panel.Children.Add(marker);
        background.Content = panel;
        await WpfFrameWaiter.UntilAsync(() => ReadCenterPixel(window)[2] > 220, "glass presentation or transition completion", cancellationToken);
        background.Content = null;
        await WpfFrameWaiter.UntilAsync(() => ReadCenterPixel(window)[1] > 175, "glass presentation or transition completion", cancellationToken);
    }

    [DesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DesktopIntegration")]
    public void GlassShader_RenderedPixelsMatchCapturedBackdrop(bool fullSurface)
    {
        RunStaAsync(async cancellationToken =>
        {
            var backdrop = new Window
            {
                Left = 40,
                Top = 70,
                Width = 600,
                Height = 350,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(32, 192, 128))
            };
            var image = new System.Windows.Controls.Image();
            var clip = new Border { Width = 320, Height = 50, ClipToBounds = true, Child = image };
            var host = new Window
            {
                Left = 160,
                Top = 190,
                Width = 320,
                Height = 50,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Content = clip
            };
            LiquidGlassController? controller = null;
            try
            {
                backdrop.Show();
                host.Show();
                host.UpdateLayout();
                double dpi = VisualTreeHelper.GetDpi(host).DpiScaleX;
                var hwnd = new WindowInteropHelper(host).Handle;
                var effect = new LiquidGlassRefractionEffect { Noise = 0, HighlightStrength = 0, Chroma = 0 };
                image.Effect = effect;
                controller = new LiquidGlassController(image, () => hwnd, () =>
                {
                    var point = clip.PointToScreen(new Point());
                    return new LiquidGlassController.CaptureRegion((int)point.X, (int)point.Y,
                        (int)(320 * dpi), (int)(50 * dpi), 0, 0);
                }, activeFps: 30);
                controller.CaptureFullSurface = fullSurface;
                Assert.True(controller.SetGpuMode(true, g =>
                {
                    effect.SrcW = controller.SurfaceWidth;
                    effect.SrcH = controller.SurfaceHeight;
                    effect.NotchW = 320 * dpi;
                    effect.NotchH = 50 * dpi;
                    effect.OffX = g.OffX;
                    effect.OffY = g.OffY;
                }));
                controller.Start();
                await WpfFrameWaiter.UntilAsync(() => controller.HasPresentedFrame, "glass presentation or transition completion", cancellationToken);
                await WpfFrameWaiter.NextAsync(cancellationToken);
                await WpfFrameWaiter.UntilAsync(() =>
                {
                    byte[] pixel = ReadRenderedCenterPixel(clip);
                    return pixel[3] > 0 && pixel[1] > 150;
                }, $"the rendered glass pixel (fullSurface={fullSurface})", cancellationToken);
            }
            finally { controller?.Stop(); host.Close(); backdrop.Close(); }
        });
    }

    private static byte[] ReadRenderedCenterPixel(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height,
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static byte[] ReadCenterPixel(SpotlightWindow window)
    {
        var effect = (LiquidGlassRefractionEffect)window.GlassBackdropImage.Effect;
        var source = Assert.IsType<D3DImage>(window.GlassBackdropImage.Source);
        var copy = ImageSourceSnapshot.Capture(source, source.PixelWidth, source.PixelHeight);
        int x = Math.Clamp((int)(effect.OffX + effect.NotchW / 2), 0, copy.PixelWidth - 1);
        int y = Math.Clamp((int)(effect.OffY + effect.NotchH / 2), 0, copy.PixelHeight - 1);
        var pixel = new byte[4];
        copy.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private sealed class GlassMorphHost : ISpotlightMorphHost
    {
        public (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius)
            GetSpotlightMorphRect() => (SystemParameters.PrimaryScreenWidth / 2 - 115, 120, 230, 32, 8, 8);
        public ImageSource? CaptureSpotlightMorphVisual() => null;
        public void SetSpotlightMorphSessionActive(bool active) { }
        public void SetSpotlightMorphActive(bool active) { }
        public void BeginSpotlightReturnHandoff(TimeSpan duration) { }
    }

    private static void RunStaAsync(Func<CancellationToken, Task> action) => SharedStaTestRunner.RunAsync(action, 120);
}
