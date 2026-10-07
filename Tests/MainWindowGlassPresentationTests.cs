using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowGlassPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void VisibleTrayAvoidsWholeContentShadowAndRestoresItWhenClosed() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowActivated = false;
        window.Show();
        window.SecondaryContent.Visibility = Visibility.Visible;
        Assert.True(window.ClipboardTrayView.IsVisible);
        Invoke(window, "ApplyGlassContentShadow", true);
        Assert.Null(window.NotchContent.Effect);
        window.SecondaryContent.Visibility = Visibility.Collapsed;
        Invoke(window, "ApplyGlassContentShadow", true);
        Assert.IsType<DropShadowEffect>(window.NotchContent.Effect);
        Invoke(window, "ApplyGlassContentShadow", false);
        Assert.Null(window.NotchContent.Effect);
    });

    [Fact]
    public void GlassMaterialsPreserveAndRestoreTheOriginalPanelAndCountdownAppearance() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var track = window.ProgressBarBg.Background;
        var timer = window.TimerControlBar.Background;
        var countdown = window.CountdownCompleteSurface.Background;
        var countdownText = window.CountdownCompleteText.Foreground;
        var restart = window.CountdownRestartBtn.Background;
        var dismiss = window.CountdownDismissBtn.Background;
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Invoke(window, "ApplyGlassContentShadow", true);
            Invoke(window, "ApplyGlassPanelMaterial", true);
            Invoke(window, "ApplyGlassToTimerBar", true);
            Invoke(window, "ApplyGlassToTimerFinishedView", true);
            Assert.IsType<DropShadowEffect>(window.NotchContent.Effect);
            Assert.IsType<DropShadowEffect>(window.AnimationThumbnailBorder.Effect);
            Assert.Equal(new Thickness(1), window.CameraSection.BorderThickness);
            Assert.Equal(new Thickness(0.5), window.CompactThumbnailRim.BorderThickness);
            Assert.Equal(Brushes.Transparent, window.CameraOverlay.Background);
            Assert.Equal(0, window.TimerControlBarShadow.Opacity);
            Assert.Same(UiPalette.PrimaryBrush, window.CountdownCompleteText.Foreground);
            Assert.Equal(new Thickness(1), window.CountdownRestartBtn.BorderThickness);
            Assert.NotSame(track, window.ProgressBarBg.Background);
            Invoke(window, "ApplyGlassContentShadow", false);
            Invoke(window, "ApplyGlassPanelMaterial", false);
            Invoke(window, "ApplyGlassToTimerBar", false);
            Invoke(window, "ApplyGlassToTimerFinishedView", false);
            Assert.Null(window.NotchContent.Effect);
            Assert.Null(window.AnimationThumbnailBorder.Effect);
            Assert.Equal(new Thickness(0), window.CameraSection.BorderThickness);
            Assert.Equal(new Thickness(0), window.CompactThumbnailRim.BorderThickness);
            Assert.Same(track, window.ProgressBarBg.Background);
            Assert.Same(timer, window.TimerControlBar.Background);
            Assert.Same(countdown, window.CountdownCompleteSurface.Background);
            Assert.Same(countdownText, window.CountdownCompleteText.Foreground);
            Assert.Same(restart, window.CountdownRestartBtn.Background);
            Assert.Same(dismiss, window.CountdownDismissBtn.Background);
            Assert.Equal(0.45, window.TimerControlBarShadow.Opacity);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpticalConfigurationClampsLayersAndReducedMotionKeepsRestingMaterial(bool gpu, bool reducedMotion) => SharedStaTestRunner.Run(() =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            using var fixture = CreateFixture();
            var window = fixture.Window;
            var settings = Field<NotchSettings>(window, "_settings");
            settings.NotchStyle = "liquidglass";
            settings.LiquidGlass = new LiquidGlassConfig
            {
                UseGpuRefraction = gpu,
                BlurAmount = 0.5,
                Opacity = 0.7,
                Noise = 0.2,
                ShadowOpacity = 0.3,
                ShadowSpread = 12,
                EdgeHighlight = 0.4,
                Specular = 0.5,
                Fresnel = 0.6,
                ChromaticAberration = 0.3,
                TouchLight = 0.7
            };
            // Configure an idle controller to test material presentation without desktop capture.
            var controller = new LiquidGlassController(window.GlassBackdropImage, () => IntPtr.Zero, () => null);
            Set(window, "_liquidGlass", controller);
            Invoke(window, "ApplyLiquidGlassConfig");
            Assert.Equal(0.7, window.GlassBackdropHost.Opacity);
            Assert.Equal(0.3, window.GlassGrainOverlay.Opacity, 5);
            Assert.Equal(Visibility.Visible, window.GlassGrainOverlay.Visibility);
            if (gpu)
            {
                Assert.Equal(7, Assert.IsType<BlurEffect>(window.GlassBackdropHost.Effect).Radius);
                Assert.Equal(0, window.GlassFresnelBorder.Opacity);
            }
            else
            {
                Assert.Null(window.GlassBackdropHost.Effect);
                Assert.True(window.GlassFresnelBorder.Opacity > 0);
                var optics = new LiquidGlassController.BackdropOptics(10, 40, 80, 2, -2, 0.8);
                Invoke(window, "UpdateDynamicFresnel", optics);
                var brush = Assert.IsType<RadialGradientBrush>(window.GlassFresnelBorder.BorderBrush);
                Assert.InRange(brush.Center.X, 0.34, 0.50);
                Assert.InRange(brush.Center.Y, 0.27, 0.41);
                Assert.InRange(window.GlassFresnelBorder.Opacity, 0, 1);
                Invoke(window, "UpdateDynamicFresnel", new LiquidGlassController.BackdropOptics(0, 0, 0, 0, 0, 0));
            }
            window.GlassBackdropHost.Visibility = Visibility.Visible;
            window.GlassBackdropHost.Measure(new Size(300, 200));
            window.GlassBackdropHost.Arrange(new Rect(0, 0, 300, 200));
            Invoke(window, "UpdateDynamicGlassParams");
            var shadow = Assert.IsType<DropShadowEffect>(window.NotchShadowWrapper.Effect);
            Assert.InRange(shadow.Opacity, 0.3, 0.55);
            Assert.InRange(shadow.BlurRadius, 12, 29);
            if (reducedMotion) Assert.Equal(12, shadow.BlurRadius);
            window.GlassDarkOverlay.Opacity = 0.9;
            Invoke(window, "UpdateDynamicGlassTint");
            Assert.Equal(0, window.GlassDarkOverlay.Opacity);
            settings.LiquidGlass.Noise = 0;
            settings.LiquidGlass.Opacity = 5;
            settings.LiquidGlass.ShadowOpacity = -1;
            settings.LiquidGlass.ShadowSpread = 200;
            settings.LiquidGlass.BlurAmount = 0;
            Invoke(window, "ApplyLiquidGlassConfig");
            Assert.Equal(1, window.GlassBackdropHost.Opacity);
            Assert.Equal(Visibility.Collapsed, window.GlassGrainOverlay.Visibility);
            Assert.Equal(0, shadow.Opacity);
            Assert.Equal(60, shadow.BlurRadius);
            Assert.Null(window.GlassBackdropHost.Effect);
            settings.NotchStyle = "default";
            Invoke(window, "ApplyLiquidGlassSkin");
            Assert.Equal(Visibility.Collapsed, window.GlassMaterialClipHost.Visibility);
            Assert.Null(window.GlassBackdropImage.Effect);
            Assert.True(double.IsNaN(window.GlassBackdropImage.Width));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ShaderGeometryUsesCurrentNotchDimensionsAndBackdropOptics() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.LiquidGlass = new LiquidGlassConfig
        {
            UseGpuRefraction = true,
            TouchLight = 0.7,
            PowerFactor = 2,
            RefractionA = 1,
            RefractionB = 2,
            RefractionC = 3,
            RefractionD = 4,
            FPower = 5,
            Noise = 0.2,
            GlowWeight = 0.3,
            GlowBias = 0.4,
            GlowEdge0 = 0.1,
            GlowEdge1 = 0.9,
            ChromaticAberration = 0.5,
            Saturation = 0.3,
            Brightness = 0.1,
            EdgeBend = 0.6,
            BevelMode = 1
        };
        Set(window, "_liquidGlass", new LiquidGlassController(window.GlassBackdropImage, () => IntPtr.Zero, () => null));
        var effect = new LiquidGlassRefractionEffect();
        Set(window, "_glassRefractionEffect", effect);
        window.NotchBorder.Width = 300;
        window.NotchBorder.Height = 40;
        window.NotchScale.ScaleX = 1.2;
        window.NotchScale.ScaleY = 1.1;
        Invoke(window, "UpdateShaderGeometryPerFrame");
        double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
        Assert.Equal(360 * dpi, effect.NotchW);
        Assert.Equal(44 * dpi, effect.NotchH);
        Assert.Equal(2, effect.PowerFactor);
        Assert.Equal(4, effect.D);
        Assert.Equal(1.3, effect.SatFactor);
        Assert.Equal(0.7, effect.HighlightStrength);
        var geometry = new LiquidGlassController.GpuGeometry(600, 300, 300, 40, 10, 20, 10, 12,
            3, 4, 5, 6, 7, 8, 0.3, 0.4, 0.5, 0.1, 0.8, 0.6, 0.7, 1.4, 0.2, 0, -100, -50);
        Invoke(window, "ApplyGpuGeometry", geometry);
        Assert.Equal(3, effect.PowerFactor);
        Assert.Equal(7, effect.D);
        Assert.Equal(1.4, effect.SatFactor);
        Invoke(window, "UpdateShaderGeometryPerFrame");
        Assert.Equal(3, effect.PowerFactor);
        Invoke(window, "OnGpuRefractionFailure", new InvalidOperationException("fixture GPU unavailable"));
        Assert.Null(window.GlassBackdropImage.Effect);
        Assert.Null(window.GlassBackdropHost.Effect);
        Assert.True(double.IsNaN(window.GlassBackdropImage.Height));
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        configureSettings: settings => settings.EnableLocalOnlyMode = true,
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
