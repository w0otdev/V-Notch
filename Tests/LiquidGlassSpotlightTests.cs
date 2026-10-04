using System;
using System.Windows;
using System.Windows.Media;
using VNotch;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class LiquidGlassSpotlightTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("liquidglass")]
    public void EntranceMetricsMatchAutoLayoutForEachShellSkin(string style)
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = style });
            var window = fixture.Window;
            window.Shell.BorderThickness = new Thickness(window.IsLiquidGlassEnabled ? 0 : 1);
            double width = SpotlightLayoutMetrics.ResolveMeasureWidth(window.ActualWidth, window.Width);
            window.Shell.Measure(new Size(width, double.PositiveInfinity));
            var target = SpotlightLayoutMetrics.CalculateEntranceSize(
                width, window.Shell.DesiredSize.Height, window.Shell.ActualHeight, window.Shell.Margin);
            window.Shell.Measure(new Size(window.Width, double.PositiveInfinity));
            double restingHeight = window.Shell.DesiredSize.Height - window.Shell.Margin.Top - window.Shell.Margin.Bottom;
            Assert.Equal(target.Height, restingHeight, precision: 3);
        });
    }

    [Fact]
    public void LiquidGlass_DefaultSettings_KeepsDefaultShellSkin()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "default" });
            var window = fixture.Window;

            Assert.False(window.IsLiquidGlassEnabled);
            Assert.Equal(Visibility.Collapsed, window.GlassMaterialClipHost.Visibility);
            Assert.Equal(Visibility.Collapsed, window.GlassBackdropHost.Visibility);
            Assert.NotEqual(Brushes.Transparent, window.Shell.Background);
        });
    }

    [Fact]
    public void LiquidGlass_Activated_AppliesLiquidGlassSkinAndLayers()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "default" });
            var window = fixture.Window;

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
        });
    }

    [Fact]
    public void LiquidGlass_CornerRadiusSync_UpdatesAllGlassLayersAndClip()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
            var window = fixture.Window;

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
        });
    }

    [Fact]
    public void LiquidGlass_ToggleSkin_SmoothlySwitchesBetweenDefaultAndLiquidGlass()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "default" });
            var window = fixture.Window;

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
        });
    }

    [Fact]
    public void LiquidGlass_SpotlightController_PropagatesSettingsToWindow()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "default" });
            SpotlightWindow? windowInstance = null;
            using var controller = new SpotlightController(() =>
            {
                windowInstance = fixture.Window;
                return windowInstance;
            });

            var host = new BackgroundWindow { Width = 100, Height = 100 };
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

            host.Close();
        });
    }

    [Fact]
    public void LiquidGlass_UpdateGlassClip_ProducesPreciseRoundedGeometry()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
            var window = fixture.Window;

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
        });
    }

    [Fact]
    public void LiquidGlass_OpticalRimLevels_FollowsConfiguration()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
            var window = fixture.Window;

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
        });
    }

    [Fact]
    public void LiquidGlass_MorphLifecycle_CleansUpOnHide()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
            var window = fixture.Window;

            window.ShowSpotlight();
            Assert.True(window.IsSpotlightOpen);
            Assert.True(window.IsLiquidGlassEnabled);

            window.HideSpotlight();
            Assert.False(window.IsSpotlightOpen);
        });
    }

    [Fact]
    public void LiquidGlass_Shutdown_DetachesAndDisposesCleanly()
    {
        RunSta(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "liquidglass" });
            var window = fixture.Window;
            window.ShowSpotlight();

            // Shutdown should not throw and should clean up gracefully
            window.Shutdown();
            Assert.False(window.IsSpotlightOpen);
        });
    }

    internal static Application CreateApplicationResources()
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!application.Resources.Contains("SFProDisplay"))
            application.Resources["SFProDisplay"] = new FontFamily("Segoe UI");
        if (!application.Resources.Contains("SFProText"))
            application.Resources["SFProText"] = new FontFamily("Segoe UI");
        if (!application.Resources.Contains("IconFont"))
            application.Resources["IconFont"] = new FontFamily("Segoe MDL2 Assets");
        return application;
    }

    internal static NotchSettings CreateMainWindowLiquidGlassSettings() => new()
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

    private static void RunSta(Action action) => SharedStaTestRunner.Run(action, 45);
}
