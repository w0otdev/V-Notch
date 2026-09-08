using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VNotch;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class LiquidGlassSpotlightTests
{
    [Fact]
    public void LiquidGlass_DefaultSettings_KeepsDefaultShellSkin()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "default" });

            Assert.False(window.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Collapsed, window.GlassMaterialClipHost.Visibility);
            Assert.Equal(Visibility.Collapsed, window.GlassBackdropHost.Visibility);
            Assert.NotEqual(Brushes.Transparent, window.Shell.Background);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_Activated_AppliesLiquidGlassSkinAndLayers()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "default" });

            var glassSettings = new NotchSettings
            {
                NotchStyle = "liquidglass",
                LiquidGlass = new LiquidGlassConfig
                {
                    BlurAmount = 0.8,
                    Opacity = 0.95,
                    Noise = 0.15,
                    EdgeHighlight = 0.8,
                    Specular = 0.75,
                    Fresnel = 0.6,
                    ChromaticAberration = 0.5,
                    TouchLight = 0.4
                }
            };

            window.ApplySettings(glassSettings);

            Assert.True(window.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Visible, window.GlassMaterialClipHost.Visibility);
            Assert.Equal(Visibility.Visible, window.GlassBackdropHost.Visibility);
            Assert.Equal(Visibility.Visible, window.GlassTintOverlay.Visibility);
            Assert.Equal(Visibility.Visible, window.GlassRimBorder.Visibility);
            Assert.Equal(Brushes.Transparent, window.Shell.Background);
            Assert.Equal(0.95, window.GlassBackdropHost.Opacity, precision: 2);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_CornerRadiusSync_UpdatesAllGlassLayersAndClip()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "liquidglass" });

            window.ShellTopCornerRadius = 18;
            window.ShellCornerRadius = 26;

            var expected = new CornerRadius(18, 18, 26, 26);
            Assert.Equal(expected, window.Shell.CornerRadius);
            Assert.Equal(expected, window.GlassBackdropHost.CornerRadius);
            Assert.Equal(expected, window.GlassTintOverlay.CornerRadius);
            Assert.Equal(expected, window.GlassRimBorder.CornerRadius);
            Assert.Equal(expected, window.GlassSpecularBorder.CornerRadius);
            Assert.Equal(expected, window.GlassGrainOverlay.CornerRadius);
            Assert.NotNull(window.GlassMaterialClipHost.Clip);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_ToggleSkin_SmoothlySwitchesBetweenDefaultAndLiquidGlass()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "default" });

            Assert.False(window.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Collapsed, window.GlassMaterialClipHost.Visibility);

            // Switch to Liquid Glass
            window.ApplySettings(new NotchSettings { NotchStyle = "liquidglass" });
            Assert.True(window.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Visible, window.GlassMaterialClipHost.Visibility);
            Assert.Equal(Brushes.Transparent, window.Shell.Background);

            // Switch back to Default
            window.ApplySettings(new NotchSettings { NotchStyle = "default" });
            Assert.False(window.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Collapsed, window.GlassMaterialClipHost.Visibility);
            Assert.NotEqual(Brushes.Transparent, window.Shell.Background);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_SpotlightController_PropagatesSettingsToWindow()
    {
        RunSta(() =>
        {
            SpotlightWindow? windowInstance = null;
            var controller = new SpotlightController(() =>
            {
                windowInstance = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "default" });
                return windowInstance;
            });

            var host = new Window { Width = 100, Height = 100 };
            var initialSettings = new NotchSettings { EnableSpotlight = true, NotchStyle = "default" };
            controller.Initialize(host, initialSettings);

            // Trigger window creation via hotkey toggle
            controller.ToggleSpotlight();
            Assert.NotNull(windowInstance);
            Assert.False(windowInstance.IsLiquidGlassEnabled);

            // Update controller with Liquid Glass settings
            var newSettings = new NotchSettings { EnableSpotlight = true, NotchStyle = "liquidglass" };
            controller.ApplySettings(newSettings);

            Assert.True(windowInstance.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Visible, windowInstance.GlassMaterialClipHost.Visibility);

            controller.Dispose();
            windowInstance.Shutdown();
            host.Close();
        });
    }

    [Fact]
    public void LiquidGlass_UpdateGlassClip_ProducesPreciseRoundedGeometry()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "liquidglass" });

            window.Shell.Width = 680;
            window.Shell.Height = 320;
            window.ShellTopCornerRadius = 20;
            window.ShellCornerRadius = 20;

            window.UpdateGlassClip();

            var clip = window.GlassMaterialClipHost.Clip as StreamGeometry;
            Assert.NotNull(clip);
            Rect bounds = clip.Bounds;
            Assert.True(bounds.Width > 0);
            Assert.True(bounds.Height > 0);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_OpticalRimLevels_FollowsConfiguration()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "liquidglass" });

            var cfg = new LiquidGlassConfig
            {
                EdgeHighlight = 0.9,
                Specular = 0.85,
                Fresnel = 0.75,
                Noise = 0.25,
                UseGpuRefraction = false // Exercise CPU optical rim stack
            };

            window.ApplySettings(new NotchSettings { NotchStyle = "liquidglass", LiquidGlass = cfg });

            Assert.True(window.GlassRimBorder.Opacity > 0);
            Assert.True(window.GlassDepthRimBorder.Opacity > 0);
            Assert.True(window.GlassFresnelBorder.Opacity > 0);
            Assert.True(window.GlassSpecularBorder.Opacity > 0);
            Assert.True(window.GlassGrainOverlay.Opacity > 0);
            Assert.Equal(Visibility.Visible, window.GlassGrainOverlay.Visibility);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_MorphLifecycle_CleansUpOnHide()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "liquidglass" });

            window.ShowSpotlight();
            Assert.True(window.IsSpotlightOpen);
            Assert.True(window.IsLiquidGlassEnabled);

            window.HideSpotlight();
            Assert.False(window.IsSpotlightOpen);
            window.Shutdown();
        });
    }

    [Fact]
    public void LiquidGlass_Shutdown_DetachesAndDisposesCleanly()
    {
        RunSta(() =>
        {
            var window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "liquidglass" });
            window.ShowSpotlight();

            // Shutdown should not throw and should clean up gracefully
            window.Shutdown();
            Assert.False(window.IsSpotlightOpen);
        });
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public void GlassShader_VisiblePixelsMatchCapturedBackdrop_InRealMainWindow()
    {
        RunSta(() =>
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
                var configureServices = typeof(App).GetMethod(
                    "ConfigureServices",
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(configureServices);
                var appConfigurationHost =
                    (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
                configureServices.Invoke(configureServices.IsStatic ? null : appConfigurationHost, [services]);
                services.AddSingleton<ISettingsService>(settingsService);
                provider = services.BuildServiceProvider();

                backdrop = new Window
                {
                    Left = SystemParameters.VirtualScreenLeft,
                    Top = SystemParameters.VirtualScreenTop,
                    Width = SystemParameters.VirtualScreenWidth,
                    Height = SystemParameters.VirtualScreenHeight,
                    WindowStyle = WindowStyle.None,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = new SolidColorBrush(Color.FromRgb(32, 192, 128))
                };
                backdrop.Show();
                backdrop.UpdateLayout();
                PumpFor(TimeSpan.FromMilliseconds(120));

                host = provider.GetRequiredService<MainWindow>();
                host.ShowActivated = false;
                host.Show();
                host.UpdateLayout();

                // The reported regression is the stable expanded MainWindow, not
                // the compact pill. Exercise its production transition before
                // comparing capture, texture, and composed desktop pixels.
                var setDebugViewState = typeof(MainWindow).GetMethod(
                    "SetDebugViewState", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(setDebugViewState);
                setDebugViewState.Invoke(host, ["MediaExpanded"]);

                var controllerField = typeof(MainWindow).GetField(
                    "_liquidGlass", BindingFlags.NonPublic | BindingFlags.Instance)!;
                PumpUntil(() => controllerField.GetValue(host) is LiquidGlassController controller &&
                    controller.HasPresentedFrame && host.NotchBorder.ActualHeight >= 140,
                    TimeSpan.FromSeconds(5));
                PumpFor(TimeSpan.FromMilliseconds(250));

                var controller = Assert.IsType<LiquidGlassController>(controllerField.GetValue(host));
                var effect = Assert.IsType<LiquidGlassRefractionEffect>(host.GlassBackdropImage.Effect);
                var source = Assert.IsType<D3DImage>(host.GlassBackdropImage.Source);
                var screenPoint = host.NotchBorder.PointToScreen(new Point(
                    host.NotchBorder.ActualWidth * 0.5,
                    host.NotchBorder.ActualHeight * 0.5));

                captureBuffer = Marshal.AllocHGlobal(4);
                magnifier = MagnifierCaptureSource.AcquireShared(IntPtr.Zero);
                Assert.True(magnifier.IsReady, "MainWindow integration requires Magnifier capture.");
                bool captured = false;
                int captureX = 0, captureY = 0;
                for (int attempt = 0; attempt < 20 && !captured; attempt++)
                {
                    captured = magnifier.CaptureInto(
                        (int)Math.Round(screenPoint.X),
                        (int)Math.Round(screenPoint.Y),
                        1, 1, captureBuffer, out captureX, out captureY);
                    if (!captured) PumpFor(TimeSpan.FromMilliseconds(25));
                }
                Assert.True(captured, "Magnifier did not return the MainWindow backdrop pixel.");

                var d3dCopy = (BitmapSource)typeof(D3DImage).GetMethod(
                    "CopyBackBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(source, null)!;
                int textureX = Math.Clamp((int)Math.Round(effect.OffX + effect.NotchW * 0.5),
                    0, d3dCopy.PixelWidth - 1);
                int textureY = Math.Clamp((int)Math.Round(effect.OffY + effect.NotchH * 0.5),
                    0, d3dCopy.PixelHeight - 1);
                var texturePixel = new byte[4];
                d3dCopy.CopyPixels(new Int32Rect(textureX, textureY, 1, 1), texturePixel, 4, 0);

                uint visiblePixel = GetScreenPixel((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
                int magnifierPixel = Marshal.ReadInt32(captureBuffer);
                double dpi = VisualTreeHelper.GetDpi(host).DpiScaleX;
                string diagnostics =
                    $"mag=B{magnifierPixel & 255},G{(magnifierPixel >> 8) & 255},R{(magnifierPixel >> 16) & 255} " +
                    $"at={captureX},{captureY}; d3d=B{texturePixel[0]},G{texturePixel[1]},R{texturePixel[2]} " +
                    $"at={textureX},{textureY}; visible=B{visiblePixel & 255},G{(visiblePixel >> 8) & 255},R{(visiblePixel >> 16) & 255}; " +
                    $"captureOrigin={controller.LastPresentedCaptureOriginX},{controller.LastPresentedCaptureOriginY}; " +
                    $"off={effect.OffX:F2},{effect.OffY:F2}; src={effect.SrcW:F0}x{effect.SrcH:F0}; " +
                    $"imageActual={host.GlassBackdropImage.ActualWidth:F2}x{host.GlassBackdropImage.ActualHeight:F2}; " +
                    $"notchActual={host.NotchBorder.ActualWidth:F2}x{host.NotchBorder.ActualHeight:F2}; dpi={dpi:F3}";
                Console.WriteLine(diagnostics);

                Assert.True(((magnifierPixel >> 8) & 255) >= 150, diagnostics);
                Assert.True(texturePixel[1] >= 150, diagnostics);
                Assert.True(((visiblePixel >> 8) & 255) >= 80, diagnostics);
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

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public void LiquidGlass_LiveDesktop_CaptureCoordinatesAndRapidReopenStayValid()
    {
        RunSta(() =>
        {
            bool reduceMotion = AnimationConfig.ReduceMotion;
            var background = new Window
            {
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(32, 192, 128))
            };
            SpotlightWindow? window = null;
            MagnifierCaptureSource? capture = null;
            IntPtr pixels = Marshal.AllocHGlobal(40 * 40 * 4);
            try
            {
                background.Show();
                background.UpdateLayout();
                PumpFor(TimeSpan.FromMilliseconds(120));
                capture = MagnifierCaptureSource.AcquireShared(IntPtr.Zero);
                Assert.True(capture.IsReady, "Desktop integration requires Magnifier capture.");
                VerifyLiveDesktopBackdropCapture(capture, background, pixels);

                AnimationConfig.SetReduceMotion(false);
                window = CreateTestSpotlightWindow(new NotchSettings { NotchStyle = "liquidglass" });
                window.MorphHostOverride = new GlassMorphHost();
                var controllerField = typeof(SpotlightWindow).GetField("_liquidGlass",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    window.ShowSpotlight();
                    var controller = (LiquidGlassController)controllerField.GetValue(window)!;
                    PumpUntil(() => controller.HasPresentedFrame, TimeSpan.FromSeconds(4));
                    VerifySpotlightSurfaceFrames(window, controller, cycle);

                    if (cycle == 0)
                    {
                        VerifyLiveDesktopDynamicUpdate(window, background, controller);
                    }
                    if (cycle == 1)
                    {
                        typeof(SpotlightWindow).GetMethod("OnGpuRefractionFailure",
                            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                                new object[] { new InvalidOperationException("Injected GPU loss") });
                        PumpUntil(() => controller.HasPresentedFrame, TimeSpan.FromSeconds(4));
                        Assert.IsType<WriteableBitmap>(window.GlassBackdropImage.Source);
                        Assert.Null(window.GlassBackdropImage.Effect);
                        Assert.True(double.IsNaN(window.GlassBackdropImage.Width));
                    }
                    // A theme switch stops and immediately restarts the controller
                    // while its native worker can still be inside CaptureInto.
                    window.ApplySettings(new NotchSettings { NotchStyle = "default" });
                    window.ApplySettings(new NotchSettings { NotchStyle = "liquidglass" });
                    PumpUntil(() => controller.HasPresentedFrame, TimeSpan.FromSeconds(4));
                    window.HideSpotlight();
                    PumpUntil(() => !window.IsSpotlightOpen, TimeSpan.FromSeconds(3));
                    Assert.False(controller.HasPresentedFrame);
                }
            }
            finally
            {
                window?.Shutdown();
                if (capture != null) MagnifierCaptureSource.ReleaseShared(IntPtr.Zero);
                Marshal.FreeHGlobal(pixels);
                background.Close();
                AnimationConfig.SetReduceMotion(reduceMotion);
                PumpFor(TimeSpan.FromMilliseconds(100));
            }
        });
    }

    private static void VerifyLiveDesktopBackdropCapture(MagnifierCaptureSource capture, Window background, IntPtr pixels)
    {
        var origin = background.PointToScreen(new Point(80, 80));
        bool captured = false;
        int pixel = 0;
        uint gdiPixel = 0;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (capture.CaptureInto((int)origin.X, (int)origin.Y, 40, 40, pixels,
                out int actualX, out int actualY))
            {
                Assert.Equal((int)origin.X, actualX);
                Assert.Equal((int)origin.Y, actualY);
                pixel = Marshal.ReadInt32(pixels, (20 * 40 + 20) * 4);
                gdiPixel = GetScreenPixel((int)origin.X + 20, (int)origin.Y + 20);
                int g = (pixel >> 8) & 255;
                if (g >= 175 && g <= 205)
                {
                    captured = true;
                    break;
                }
            }
            PumpFor(TimeSpan.FromMilliseconds(40));
        }
        Assert.True(captured, $"Expected green backdrop pixel, got 0x{pixel:X8}, gdi=0x{gdiPixel:X8} at {(int)origin.X + 20},{(int)origin.Y + 20}.");
        Assert.InRange((pixel >> 8) & 255, 175, 205);
        Assert.InRange((pixel >> 16) & 255, 20, 45);
        Assert.InRange(pixel & 255, 110, 145);
    }

    private static void VerifySpotlightSurfaceFrames(SpotlightWindow window, LiquidGlassController controller, int cycle)
    {
        var surface = window.GlassBackdropImage.Source;
        Assert.IsType<D3DImage>(surface);
        for (int frame = 0; frame < 20; frame++)
        {
            PumpFor(TimeSpan.FromMilliseconds(20));
            Assert.Same(surface, window.GlassBackdropImage.Source);
            Assert.True(controller.HasPresentedFrame);
            var effect = Assert.IsType<LiquidGlassRefractionEffect>(window.GlassBackdropImage.Effect);
            double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
            Assert.True(effect.SrcW >= window.GlassBackdropHost.ActualWidth * dpi);
            Assert.True(effect.SrcH >= window.GlassBackdropHost.ActualHeight * dpi);
            var copy = (BitmapSource?)typeof(D3DImage).GetMethod("CopyBackBuffer",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, null);
            Assert.NotNull(copy);
            int sx = Math.Clamp((int)(effect.OffX + effect.NotchW / 2), 0, copy.PixelWidth - 1);
            int sy = Math.Clamp((int)(effect.OffY + effect.NotchH / 2), 0, copy.PixelHeight - 1);
            var sample = new byte[4];
            copy.CopyPixels(new Int32Rect(sx, sy, 1, 1), sample, 4, 0);
            if (sample[1] < 175 || sample[1] > 205)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(copy));
                using var file = File.Create(Path.Combine(Path.GetTempPath(), "vnotch-glass-failed-frame.png"));
                encoder.Save(file);
            }
            Assert.True(sample[1] >= 175 && sample[1] <= 205,
                $"cycle={cycle} frame={frame} pixel={sample[2]},{sample[1]},{sample[0]} " +
                $"screen={controller.LastPresentedCaptureOriginX + sx},{controller.LastPresentedCaptureOriginY + sy} " +
                $"source={copy.PixelWidth}x{copy.PixelHeight} offset={effect.OffX},{effect.OffY}");
            Assert.InRange(sample[2], (byte)20, (byte)45);
            if (frame == 8) window.HideSpotlight();
            if (frame == 11) window.ToggleFromHotkey();
        }
    }

    private static void VerifyLiveDesktopDynamicUpdate(SpotlightWindow window, Window background, LiquidGlassController controller)
    {
        PumpUntil(() => double.IsNaN(window.Shell.Height), TimeSpan.FromSeconds(3));
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
        PumpUntil(() => ReadCenterPixel(window)[2] > 220, TimeSpan.FromSeconds(4));
        background.Content = null;
        PumpUntil(() => ReadCenterPixel(window)[1] > 175, TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DesktopIntegration")]
    public void GlassShader_VisiblePixelsMatchCapturedBackdrop(bool fullSurface)
    {
        RunSta(() =>
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
                PumpUntil(() => controller.HasPresentedFrame, TimeSpan.FromSeconds(3));
                PumpFor(TimeSpan.FromMilliseconds(200));
                var point = clip.PointToScreen(new Point(160, 25));
                IntPtr dc = GetDCForGlassTest(IntPtr.Zero);
                uint color;
                try { color = GetPixelForGlassTest(dc, (int)point.X, (int)point.Y); }
                finally { ReleaseDCForGlassTest(IntPtr.Zero, dc); }
                Assert.True(((color >> 8) & 255) > 150,
                    $"Rendered glass is dark: RGB={color & 255},{(color >> 8) & 255},{(color >> 16) & 255}, full={fullSurface}");
            }
            finally { controller?.Stop(); host.Close(); backdrop.Close(); }
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr GetDCForGlassTest(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDCForGlassTest(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint GetPixelForGlassTest(IntPtr dc, int x, int y);

    private static uint GetScreenPixel(int x, int y)
    {
        IntPtr dc = GetDCForGlassTest(IntPtr.Zero);
        try
        {
            return GetPixelForGlassTest(dc, x, y);
        }
        finally
        {
            ReleaseDCForGlassTest(IntPtr.Zero, dc);
        }
    }

    private static Application CreateApplicationResources()
    {
        if (Application.Current != null) return Application.Current;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
        application.Resources["SFProText"] = new FontFamily("Segoe UI");
        application.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        return application;
    }

    private static NotchSettings CreateMainWindowLiquidGlassSettings() => new()
    {
        Width = 300,
        Height = 40,
        CornerRadius = 20,
        NotchStyle = "liquidglass",
        EnableSpotlight = false,
        AutoCheckUpdates = false,
        EnableWeather = false,
        LiquidGlass = new LiquidGlassConfig
        {
            BlurAmount = 0.35,
            Refraction = 1.15,
            EdgeBend = 1.7,
            ChromaticAberration = 0.2,
            EdgeHighlight = 0,
            TouchLight = 0.9,
            Specular = 0.22,
            Fresnel = 0.46,
            Distortion = 1,
            CornerRadius = 20,
            ZRadius = 0.1,
            Opacity = 1,
            Saturation = 0,
            Brightness = 0,
            ShadowOpacity = 1,
            ShadowSpread = 18,
            BevelMode = 0,
            TargetFps = 150,
            Variant = 0,
            PowerFactor = 3,
            RefractionA = 0.7,
            RefractionB = 2.3,
            RefractionC = 5.2,
            RefractionD = 6.9,
            FPower = 1,
            Noise = 0.08,
            GlowWeight = 0.692,
            GlowBias = -0.04,
            GlowEdge0 = 0.441,
            GlowEdge1 = -0.474,
            HideFromScreenCapture = true,
            UseGpuRefraction = true
        }
    };

    private static byte[] ReadCenterPixel(SpotlightWindow window)
    {
        var effect = (LiquidGlassRefractionEffect)window.GlassBackdropImage.Effect;
        var copy = (BitmapSource)typeof(D3DImage).GetMethod("CopyBackBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window.GlassBackdropImage.Source, null)!;
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

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        long until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "Glass did not present/finish before the deadline.");
            PumpFor(TimeSpan.FromMilliseconds(10));
        }
    }

    private static SpotlightWindow CreateTestSpotlightWindow(NotchSettings settings)
    {
        string usagePath = Path.Combine(Path.GetTempPath(), $"vnotch-test-lg-{Guid.NewGuid():N}.json");
        var service = new SpotlightSearchService([new FakeProvider()]);
        var viewModel = new SpotlightViewModel(service, new SpotlightUsageStore(usagePath, () => DateTime.UtcNow));
        return new SpotlightWindow(viewModel, new SpotlightLauncher(), settings)
        {
            Opacity = 0,
            SuppressForegroundActivationForTests = true
        };
    }

    private sealed class FakeProvider : ISpotlightProvider
    {
        public bool IsAvailable => true;

        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SpotlightSearchItem>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<SpotlightSearchItem>>(
                Array.Empty<SpotlightSearchItem>());
    }

    private static void RunSta(Action action) => SharedStaTestRunner.Run(action, 45);
}
