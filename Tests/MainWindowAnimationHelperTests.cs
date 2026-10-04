using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;
using Path = System.Windows.Shapes.Path;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowAnimationHelperTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkipArrowsReturnToTheirOriginalPositionAndVisibilityAfterEachAnimation(bool next) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var incoming = new Path { Opacity = 0 };
        var outgoing = new Path { Opacity = 1 };
        string method = next ? "PlayNextSkipAnimation" : "PlayPrevSkipAnimation";
        for (int i = 0; i < 2; i++)
        {
            Invoke(fixture.Window, method, incoming, outgoing);
            await Task.Delay(600, ct);
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(1, outgoing.Opacity);
            Assert.Equal(0, incoming.Opacity);
            Assert.Equal(0, ((TranslateTransform)outgoing.RenderTransform).X);
            Assert.Equal(0, ((TranslateTransform)incoming.RenderTransform).X);
            Assert.False(outgoing.HasAnimatedProperties);
        }
        Invoke(fixture.Window, method);
        await Task.Delay(600, ct);
        Assert.Equal(1, next ? fixture.Window.NextArrow1.Opacity : fixture.Window.PrevArrow2.Opacity);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModeTransitionInterpolatesGeometryAndCommitsCollapsedTarget(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableDynamicIslandMode = island;
        settings.DynamicIslandWidth = 240;
        settings.DynamicIslandHeight = 32;
        Set(window, "_collapsedWidth", island ? 240d : 300d);
        Set(window, "_collapsedHeight", island ? 32d : 40d);
        Set(window, "_cornerRadiusCollapsed", island ? 16d : 20d);
        Invoke(window, "AnimateModeTransition", 60);
        Assert.True(Field<bool>(window, "_isModeTransitioning"));
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isModeTransitioning"), "mode transition final geometry", ct);
        Assert.Equal(island ? 240 : 300, window.NotchBorder.Width);
        Assert.Equal(island ? 32 : 40, window.NotchBorder.Height);
        Assert.Equal(island ? new CornerRadius(16) : new CornerRadius(0, 0, 20, 20), window.NotchBorder.CornerRadius);
        Assert.Equal(window.NotchBorder.CornerRadius, window.InnerClipBorder.CornerRadius);
        Assert.Equal(island ? Visibility.Collapsed : Visibility.Visible, window.LeftEar.Visibility);
        Assert.Equal(island ? Visibility.Collapsed : Visibility.Visible, window.RightEar.Visibility);
        window.ModeTransitionT = 0.5;
        Assert.Equal(270, window.NotchBorder.Width);
        Assert.Equal(36, window.NotchBorder.Height);
        Assert.Equal(new CornerRadius(8, 8, 18, 18), window.NotchBorder.CornerRadius);
        Invoke(window, "FinalizeModeTransition", island);
        Assert.Equal(island ? 240 : 300, window.NotchBorder.Width);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompactArtworkHoverExpandsAndRestoresGeometryAndTextMarquee(bool island) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Field<NotchSettings>(window, "_settings").EnableDynamicIslandMode = island;
        Set(window, "_isMusicCompactMode", true);
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = new string('W', 120), CurrentArtist = "Fixture artist" });
        Set(window, "_isCompactThumbnailHovered", true);
        Invoke(window, "AnimateThumbnailHover", true);
        await WpfFrameWaiter.UntilAsync(() => window.CompactThumbnailScale.ScaleX == (island ? 1.28 : 1.5), "compact artwork hover geometry", ct);
        Assert.Equal(Visibility.Visible, window.CompactHoverInfo.Visibility);
        Assert.NotNull(window.CompactHoverInfo.OpacityMask);
        Assert.True(window.CompactTitleMarqueeTranslate.HasAnimatedProperties);
        Assert.True(window.NotchBorder.Width > Field<double>(window, "_collapsedWidth"));
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = "Short" });
        Invoke(window, "UpdateCompactMarquee");
        Assert.Null(window.CompactHoverInfo.OpacityMask);
        Assert.False(window.CompactTitleMarqueeTranslate.HasAnimatedProperties);
        Set(window, "_isCompactThumbnailHovered", false);
        Invoke(window, "AnimateThumbnailHover", false);
        await WpfFrameWaiter.UntilAsync(() => window.CompactHoverInfo.Visibility == Visibility.Collapsed && window.CompactThumbnailScale.ScaleX == 1, "compact artwork hover exit", ct);
        Assert.Equal(Field<double>(window, "_collapsedWidth"), window.NotchBorder.Width);
        Invoke(window, "ApplyCompactTitleContainerWidth", double.NaN);
        Assert.True(double.IsFinite(window.CompactTitleScrollContainer.Width));
    });

    [Theory]
    [InlineData("PlayButtonPressAnimation")]
    [InlineData("PlayGentleButtonPressAnimation")]
    public void ButtonPressCreatesAReusableScaleAndReturnsToUnitSize(string method) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var button = new Border();
        Invoke(fixture.Window, method, button);
        var transform = Assert.IsType<ScaleTransform>(button.RenderTransform);
        await WpfFrameWaiter.UntilAsync(() => transform.ScaleX < 0.99, "button press squish", ct);
        await WpfFrameWaiter.UntilAsync(() => transform.ScaleX == 1 && transform.ScaleY == 1, "button press restored", ct);
        Assert.Equal(new Point(0.5, 0.5), button.RenderTransformOrigin);
        Invoke(fixture.Window, "AnimateButtonScale", transform, 1.1d);
        await WpfFrameWaiter.UntilAsync(() => transform.ScaleX == 1.1 && transform.ScaleY == 1.1, "button hover scale", ct);
    });

    [Fact]
    public void IconSwitchHidesThePreviousGlyphAndRestoresItsTransformForReuse() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var from = new Canvas();
        var to = new Canvas { Visibility = Visibility.Collapsed };
        Invoke(fixture.Window, "AnimateIconSwitch", from, to, TimeSpan.FromMilliseconds(100), new CubicEase());
        await WpfFrameWaiter.UntilAsync(() => from.Visibility == Visibility.Collapsed && to.Opacity == 1, "playback icon switched", ct);
        Assert.Equal(Visibility.Visible, to.Visibility);
        Assert.Equal(1, ((ScaleTransform)from.RenderTransform).ScaleX);
        Assert.Equal(1, ((ScaleTransform)to.RenderTransform).ScaleY);
        Assert.False(from.HasAnimatedProperties);
        Invoke(fixture.Window, "AnimateIconSwitch", to, from, TimeSpan.FromMilliseconds(100), new CubicEase());
        await WpfFrameWaiter.UntilAsync(() => to.Visibility == Visibility.Collapsed && from.Opacity == 1, "playback icon switch reversed", ct);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProgressHoverEnlargesTheBarAndTimeLabelsThenRestoresControls(bool blur) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Field<NotchSettings>(window, "_settings").EnableBlurEffects = blur;
        Invoke(window, "AnimateProgressBarHover", true);
        await WpfFrameWaiter.UntilAsync(() => window.ProgressBarBg.Height == 10 && Math.Abs(window.ProgressBarMainScale.ScaleX - 1.04) < 0.000001 && Math.Abs(window.MediaControls.Opacity - 0.45) < 0.000001, "progress hover targets", ct);
        Assert.Equal(5, window.ProgressBarClip.RadiusX);
        var labels = Assert.IsType<TransformGroup>(window.CurrentTimeText.RenderTransform);
        Assert.Equal(1.22, ((ScaleTransform)labels.Children[0]).ScaleX);
        Assert.Equal(3, ((TranslateTransform)labels.Children[1]).Y);
        if (blur) Assert.Equal(4, Assert.IsType<System.Windows.Media.Effects.BlurEffect>(window.MediaControls.Effect).Radius);
        else Assert.Null(window.MediaControls.Effect);
        Invoke(window, "AnimateProgressBarHover", false);
        await WpfFrameWaiter.UntilAsync(() => window.ProgressBarBg.Height == 4 && window.ProgressBarMainScale.ScaleX == 1 && window.MediaControls.Opacity == 1, "progress hover restored", ct);
        Assert.Equal(2, window.ProgressBarClip.RadiusY);
        Assert.Equal(1, ((ScaleTransform)labels.Children[0]).ScaleY);
        Assert.Equal(0, ((TranslateTransform)labels.Children[1]).Y);
        Field<NotchSettings>(window, "_settings").EnableBlurEffects = false;
        Invoke(window, "AnimateProgressBarHover", true);
        Assert.Null(window.MediaControls.Effect);
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void StatusRevealHonorsBatteryVisibilityAndAnimatesUpdateAndSettingsIcons(bool island, bool battery) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableDynamicIslandMode = island;
        settings.ShowBatteryIndicator = battery;
        Set(window, "_isUpdateAvailable", true);
        Invoke(window, "AnimateStatusBarReveal", true);
        await WpfFrameWaiter.UntilAsync(() => window.SettingsRotate.Angle == 45 && window.SettingsScale.ScaleX == 1, "status icons revealed", ct);
        Assert.Equal(battery ? Visibility.Visible : Visibility.Collapsed, window.BatterySection.Visibility);
        Assert.Equal(battery, window.BatterySection.IsHitTestVisible);
        Assert.Equal(island ? 5 : 0, window.NavIconsTranslate.Y);
        Assert.Equal(1, window.UpdateNotificationButton.Opacity);
        Assert.True(window.UpdateNotificationButton.IsHitTestVisible);
        Invoke(window, "AnimateStatusBarReveal", false);
        await WpfFrameWaiter.UntilAsync(() => window.SettingsButton.Opacity == 0 && window.NavIconsPanel.Opacity == 0 && window.SettingsRotate.Angle == 20 && window.UpdateNotificationButton.Opacity == 0, "status icons hidden", ct);
        Assert.False(window.UpdateNotificationButton.IsHitTestVisible);
        Assert.Equal(0, window.UpdateNotificationButton.Opacity);
        Assert.Equal(-6, window.SettingsTranslate.Y);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingsEjectAndAbsorbFeedbackReturnsToCollapsedGeometry(bool eject) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        if (eject) window.PlaySettingsEjectAnimation(); else window.PlaySettingsAbsorbAnimation();
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width > 302, "settings feedback opens pill", ct);
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width == 300 && window.NotchBorder.Height == 40 && window.NotchScale.ScaleY == 1, "settings feedback restores pill", ct);
        Assert.Equal(1, window.NotchShadowScale.ScaleY);
        Assert.Equal(window.NotchBorder.CornerRadius, window.NotchBorderShadow.CornerRadius);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture()
    {
        var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false,
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
        fixture.Window.NotchBorder.Width = 300;
        fixture.Window.NotchBorder.Height = 40;
        return fixture;
    }
    private static T Field<T>(MainWindow window, string name) => (T)(typeof(MainWindow).GetField(name, Private)?.GetValue(window) ?? typeof(MainWindow).GetProperty(name, Private)!.GetValue(window))!;
    private static void Set(MainWindow window, string name, object value)
    {
        if (typeof(MainWindow).GetField(name, Private) is { } field) field.SetValue(window, value);
        else typeof(MainWindow).GetProperty(name, Private)!.SetValue(window, value);
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethods(Private).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
}
