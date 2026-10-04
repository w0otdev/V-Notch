using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowMorphHandoffTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, 120)]
    [InlineData(true, 120)]
    [InlineData(false, 0)]
    public void SpotlightReturnsTheExactOpacityAndInputStateItBorrowed(bool reducedMotion, int milliseconds) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create();
        var window = fixture.Window;
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reducedMotion);
        try
        {
            window.NotchWrapper.Opacity = .7;
            window.NotchShadowWrapper.Opacity = .4;
            window.NotchWrapper.IsHitTestVisible = true;
            window.SetSpotlightMorphSessionActive(true);
            window.SetSpotlightMorphActive(true);
            window.SetSpotlightMorphActive(true);
            Assert.Equal(0, window.NotchWrapper.Opacity);
            Assert.Equal(0, window.NotchShadowWrapper.Opacity);
            Assert.False(window.NotchWrapper.IsHitTestVisible);
            window.BeginSpotlightReturnHandoff(TimeSpan.FromMilliseconds(milliseconds));
            Assert.Equal(.7, window.NotchWrapper.Opacity);
            Assert.Equal(.4, window.NotchShadowWrapper.Opacity);
            Assert.False(window.NotchWrapper.IsHitTestVisible);
            if (!reducedMotion && milliseconds > 0)
            {
                Assert.IsType<BlurEffect>(window.SpotlightReturnContentHost.Effect);
                await WpfFrameWaiter.UntilAsync(() => ((BlurEffect)window.SpotlightReturnContentHost.Effect).Radius == 0 && window.SpotlightReturnContentHost.Opacity == 1, "Spotlight return content sharpening", ct);
            }
            window.SetSpotlightMorphActive(false);
            window.SetSpotlightMorphSessionActive(false);
            Assert.True(window.NotchWrapper.IsHitTestVisible);
            Assert.Null(window.SpotlightReturnContentHost.Effect);
            Assert.Equal(1, window.SpotlightReturnContentHost.Opacity);
            Assert.False(Get<bool>(window, "_spotlightMorphOwnsNotchVisibility"));
            Assert.False(Get<bool>(window, "_spotlightReturnHandoffActive"));
            window.BeginSpotlightReturnHandoff(TimeSpan.FromMilliseconds(120));
            Assert.False(Get<bool>(window, "_spotlightReturnHandoffActive"));
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReopeningSpotlightDuringReturnClearsTheOldAnimationWithoutLosingRestoreValues() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create();
        var window = fixture.Window;
        window.NotchWrapper.Opacity = .65;
        window.NotchShadowWrapper.Opacity = .3;
        window.NotchWrapper.IsHitTestVisible = false;
        window.SetSpotlightMorphActive(true);
        window.BeginSpotlightReturnHandoff(TimeSpan.FromSeconds(1));
        await WpfFrameWaiter.NextAsync(ct);
        window.SetSpotlightMorphSessionActive(true);
        window.SetSpotlightMorphActive(true);
        Assert.Null(window.SpotlightReturnContentHost.Effect);
        Assert.Equal(0, window.NotchWrapper.Opacity);
        window.SetSpotlightMorphActive(false);
        window.SetSpotlightMorphSessionActive(false);
        Assert.Equal(.65, window.NotchWrapper.Opacity);
        Assert.Equal(.3, window.NotchShadowWrapper.Opacity);
        Assert.False(window.NotchWrapper.IsHitTestVisible);
        Assert.Equal(1, window.NotchScale.ScaleX);
        Assert.Equal(1, window.NotchShadowScale.ScaleY);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MorphGeometryUsesTheCurrentNotchSizeAndMaterialCornerRadii(bool island) => SharedStaTestRunner.RunAsync(ct =>
    {
        using var fixture = Create();
        var window = fixture.Window;
        Get<NotchSettings>(window, "_settings").EnableDynamicIslandMode = island;
        window.NotchBorder.Width = 300;
        window.NotchBorder.Height = 40;
        window.NotchBorder.Measure(new Size(300, 40));
        window.NotchBorder.Arrange(new Rect(0, 0, 300, 40));
        window.NotchBorder.CornerRadius = new CornerRadius(22, 23, 24, 25);
        var geometry = window.GetSpotlightMorphRect();
        Assert.Equal(300, geometry.Width);
        Assert.Equal(40, geometry.Height);
        Assert.Equal(island ? 22 : 0, geometry.TopCornerRadius);
        Assert.Equal(24, geometry.BottomCornerRadius);
        Assert.True(double.IsFinite(geometry.Left));
        Assert.True(double.IsFinite(geometry.Top));
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    });

    [Fact]
    public void ReturnBounceAndSettingsAndBatteryHoverSettleAtTheirRestingValues() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = Create();
        var window = fixture.Window;
        window.PlayNotchReturnBounce();
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.NotchScale.ScaleX - 1) < .00001 && window.NotchScale.HasAnimatedProperties, "notch return bounce", ct);
        var mouse = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
        Invoke(window, "SettingsButton_MouseEnter", window, mouse);
        Invoke(window, "AnimateBatteryHover", true);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.SettingsRotate.Angle - 132) < .00001 && Math.Abs(window.BatterySectionScale.ScaleX - 1.1) < .00001, "settings and battery hover", ct);
        Assert.NotNull(window.SettingsButton.CacheMode);
        Invoke(window, "SettingsButton_MouseLeave", window, mouse);
        Invoke(window, "AnimateBatteryHover", false);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.SettingsRotate.Angle - 45) < .00001 && Math.Abs(window.SettingsScale.ScaleX - 1) < .00001 && Math.Abs(window.BatterySectionScale.ScaleY - 1) < .00001, "settings and battery resting state", ct);
        Assert.Equal(Colors.Transparent, ((SolidColorBrush)window.SettingsButton.Background).Color);
        Assert.Equal(1, window.NotchScale.ScaleX, 5);
        Assert.Equal(1, window.NotchShadowScale.ScaleY, 5);
    });

    private static GreetingAcceptanceTests.MainWindowFixture Create() => new("en", false, settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; }, services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
}
