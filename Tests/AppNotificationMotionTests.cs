using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationMotionTests
{
    [Fact]
    public void ContentAppearsImmediatelyWithTheMainViewOpeningMotion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        await Task.Delay(120, ct);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.True(window.AppNotificationBanner.Opacity > .2, "The notification must reveal with the shell, without a separate waiting phase.");
        await WpfFrameWaiter.UntilAsync(() => !DependencyPropertyHelper.GetValueSource(window.NotchBorder, FrameworkElement.WidthProperty).IsAnimated,
            "notification finishes the main view geometry motion", ct);
    });

    [Fact]
    public void EntranceStartsFromTheCurrentNotchWithoutFlashingTheFinalFrame() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        double initialWidth = window.NotchBorder.Width;
        double initialHeight = window.NotchBorder.Height;
        window.ShowAppNotification(Notification());
        Assert.Equal(initialWidth, window.NotchBorder.Width);
        Assert.Equal(initialHeight, window.NotchBorder.Height);
        Assert.Equal(0, window.AppNotificationBanner.Opacity);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBanner.Opacity is > .2 and < .8,
            "notification reveals its content during the morph", ct);
        Assert.Equal(1, window.AppNotificationAppNameText.Opacity);
        Assert.Equal(1, window.AppNotificationTitleText.Opacity);
        Assert.Equal(1, window.AppNotificationBodyText.Opacity);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
        Assert.Equal(0, window.AppNotificationIconRotate.Angle);
    });

    [Fact]
    public void HoverDuringEntranceKeepsTheSameSizeAndNeverFloatsTheContent() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        double width = Get<double>(window, "_appNotificationBaseWidth");
        window.AnimateAppNotificationHover(true);
        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width == width && window.AppNotificationBannerTranslate.Y == 0,
            "notification entrance settles at its original size", ct);
        window.AnimateAppNotificationHover(false);
        window.AnimateAppNotificationHover(true);
        await Task.Delay(350, ct);
        Assert.Equal(width, window.NotchBorder.Width);
        Assert.Equal(0, window.AppNotificationBannerTranslate.Y);
        Assert.Equal(1, window.AppNotificationBannerScale.ScaleX);
        Assert.Equal(1, window.NotchScale.ScaleX);
        Assert.Equal(1, window.AppNotificationTitleText.Opacity);
        Assert.InRange(window.AppNotificationHoverSurface.Opacity, .03, .04);
        Assert.False(window.AppNotificationHoverSurface.IsHitTestVisible);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
        Assert.Null(window.FindName("AppNotificationCloseButton"));
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(window.AppNotificationHoverSurface.Background).Color);
        Assert.Equal(new Thickness(0), window.AppNotificationHoverSurface.BorderThickness);
        Assert.Null(window.AppNotificationHoverSurface.BorderBrush);
        Assert.Equal(new Thickness(0), window.HoverGlow.BorderThickness);
        Assert.Null(window.HoverGlow.BorderBrush);

        window.AnimateAppNotificationHover(false);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationHoverSurface.Opacity == 0 &&
            window.AppNotificationIconScale.ScaleX == 1,
            "notification hover feedback returns to rest", ct);
        Assert.Equal(width, window.NotchBorder.Width);
    });

    [Fact]
    public void PressAndReleaseKeepTextAtFullSizeAndRestoreTheHoveredSurface() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBanner.Opacity == 1 &&
            window.AppNotificationBannerTranslate.Y == 0, "notification is ready to interact with", ct);
        window.AnimateAppNotificationHover(true);
        window.BeginAppNotificationInteraction(new Point(0, 0), 0);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationPressTranslate.Y == 1,
            "press receives a small positional response", ct);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        Assert.Equal(1, window.AppNotificationBannerScale.ScaleX);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
        Assert.InRange(window.AppNotificationHoverSurface.Opacity, .06, .07);
        Assert.True(window.EndAppNotificationInteraction(new Point(1, 0), 250));
        window.AnimateAppNotificationHover(false);
        window.AnimateAppNotificationHover(true);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationPressTranslate.Y == 0 &&
            window.AppNotificationHoverSurface.Opacity == .035, "release settles into the current hover state", ct);
        Assert.Equal(1, window.AppNotificationBannerScale.ScaleX);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
        Assert.True(window.IsAppNotificationBannerActive);
    });

    [Fact]
    public void HoverPausesAndResumesTheRemainingDismissTime() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(autoDismiss: true);
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationProgressScale.ScaleX < .98, "dismiss countdown starts", ct);
        window.AnimateAppNotificationHover(true);
        double progress = window.AppNotificationProgressScale.ScaleX;
        int remaining = Get<int>(window, "_appNotificationRemainingMs");
        Assert.Null(Get<object?>(window, "_appNotificationDismissTimer"));
        await Task.Delay(180, ct);
        Assert.Equal(progress, window.AppNotificationProgressScale.ScaleX, 5);
        window.AnimateAppNotificationHover(false);
        Assert.NotNull(Get<object?>(window, "_appNotificationDismissTimer"));
        Assert.InRange(Get<int>(window, "_appNotificationRemainingMs"), remaining - 10, remaining);
        await Task.Delay(220, ct);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.True(window.AppNotificationProgressScale.ScaleX < progress - .01,
            $"progress={progress}->{window.AppNotificationProgressScale.ScaleX}; hover={Get<bool>(window, "_isAppNotificationHovered")}; remaining={Get<int>(window, "_appNotificationRemainingMs")}; timer={Get<object?>(window, "_appNotificationDismissTimer") != null}");
    });

    [Fact]
    public void ShortDragReturnsTheShellAndShadowWithoutActivatingOrDismissing() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        window.BeginAppNotificationInteraction(new Point(0, 0), 0);
        window.UpdateAppNotificationInteraction(new Point(28, 10), 200);
        Assert.True(window.AppNotificationShellTranslate.X > 0);
        Assert.Equal(window.AppNotificationShellTranslate.X, window.AppNotificationShadowTranslate.X);
        Assert.False(window.EndAppNotificationInteraction(new Point(28, 10), 400));
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationShellTranslate.X == 0 &&
            window.AppNotificationShadowTranslate.X == 0 && window.AppNotificationBannerScale.ScaleX == 1,
            "short notification drag springs back", ct);
        Assert.True(window.IsAppNotificationBannerActive);
        Assert.Equal(0, window.AppNotificationShellTranslate.Y);
        Assert.Equal(1, window.AppNotificationBanner.Opacity);
    });

    [Theory]
    [InlineData(100, 300)]
    [InlineData(-100, 300)]
    [InlineData(24, 20)]
    public void ALongDragOrQuickFlickDismissesTheNotification(double distance, long elapsed) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        window.BeginAppNotificationInteraction(new Point(0, 0), 0);
        window.UpdateAppNotificationInteraction(new Point(distance, 0), elapsed);
        Assert.False(window.EndAppNotificationInteraction(new Point(distance, 0), elapsed + 1));
        await WpfFrameWaiter.UntilAsync(() => !window.IsAppNotificationBannerActive, "notification swipe dismisses", ct);
        Assert.Equal(Visibility.Collapsed, window.AppNotificationBanner.Visibility);
        Assert.Equal(0, window.AppNotificationShellTranslate.X);
        Assert.Equal(0, window.AppNotificationShadowTranslate.X);
    });

    [Fact]
    public void ReplacementDuringExitSurvivesThePreviousCompletion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        window.AnimateAppNotificationHover(true);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationHoverSurface.Opacity > .02,
            "notification hover surface appears before dismissal", ct);
        window.DismissAppNotificationBanner();
        window.ShowAppNotification(new AppNotificationInfo { AppName = "Second app", Title = "Latest notification", Body = "New content" });
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBanner.Opacity == 1 && window.AppNotificationBodyText.Opacity == 1,
            "replacement notification enters", ct);
        await Task.Delay(250, ct);
        Assert.True(window.IsAppNotificationBannerActive);
        Assert.Equal("Latest notification", window.AppNotificationTitleText.Text);
        Assert.Equal(Visibility.Visible, window.AppNotificationBanner.Visibility);
        Assert.False(Get<bool>(window, "_isAppNotificationBannerExiting"));
        Assert.Equal(0, window.AppNotificationHoverSurface.Opacity);
        Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
    });

    [Fact]
    public void ReducedMotionKeepsTranslationsAndScaleStillAndEscapeDismissesImmediately() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var window = fixture.Window;
            window.ShowAppNotification(Notification());
            window.AnimateAppNotificationHover(true);
            Assert.Equal(0, window.AppNotificationBannerTranslate.Y);
            Assert.Equal(0, window.AppNotificationTitleTranslate.Y);
            Assert.Equal(0, window.AppNotificationIconRotate.Angle);
            Assert.Equal(1, window.AppNotificationIconScale.ScaleX);
            window.BeginAppNotificationInteraction(new Point(0, 0), 0);
            Assert.Equal(0, window.AppNotificationPressTranslate.Y);
            var keyboard = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent };
            window.AppNotificationBanner.RaiseEvent(keyboard);
            Assert.False(window.IsAppNotificationBannerActive);
            Assert.Equal(Visibility.Collapsed, window.AppNotificationBanner.Visibility);
            Assert.Equal(Visibility.Collapsed, window.AppNotificationHoverSurface.Visibility);
            Assert.Equal(0, window.AppNotificationHoverSurface.Opacity);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static AppNotificationInfo Notification() => new()
        { AppName = "Test notification app", Title = "Your download is ready", Body = "Open the application to continue." };
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(bool autoDismiss = false) => new("en", greeting: false,
        configureSettings: settings =>
        {
            settings.EnableLocalOnlyMode = true;
            settings.ShowSystemNotifications = true;
            settings.AutoDismissNotifications = autoDismiss;
            settings.NotificationDuration = 5000;
        }, configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Get<T>(MainWindow window, string field) => (T)typeof(MainWindow)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
