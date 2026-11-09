using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationBlurTests
{
    [Fact]
    public void EntranceCrossfadesABlurredSnapshotIntoCrispLiveContent() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        var snapshot = Assert.IsAssignableFrom<BitmapSource>(window.AppNotificationBlurCover.Source);
        Assert.True(snapshot.IsFrozen);
        byte[] pixels = new byte[snapshot.PixelWidth * snapshot.PixelHeight * 4];
        snapshot.CopyPixels(pixels, snapshot.PixelWidth * 4, 0);
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 8);
        Assert.Equal(Visibility.Visible, window.AppNotificationBlurCover.Visibility);
        Assert.Equal(0, window.AppNotificationContent.Opacity);
        Assert.Equal(1, window.AppNotificationBlurCover.Opacity);
        Assert.Null(window.AppNotificationContent.Effect);
        var blur = Assert.IsType<BlurEffect>(window.AppNotificationBlurCover.Effect);
        Assert.False(blur.HasAnimatedProperties);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationContent.Opacity is > .1 and < .95,
            "blur cover crossfades into the notification", ct);
        Assert.InRange(window.AppNotificationContent.Opacity + window.AppNotificationBlurCover.Opacity, .99, 1.01);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBlurCover.Source == null,
            "completed entrance releases the blur surface", ct);
        Assert.Equal(1, window.AppNotificationContent.Opacity);
        Assert.Equal(Visibility.Collapsed, window.AppNotificationBlurCover.Visibility);
    });

    [Fact]
    public void ExitBlursTheContentBeforeFadingAndReleasesTheCover() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBlurCover.Source == null && window.AppNotificationBanner.Opacity == 1,
            "notification enters before dismissal", ct);
        window.DismissAppNotificationBanner();
        Assert.NotNull(window.AppNotificationBlurCover.Source);
        Assert.Equal(Visibility.Visible, window.AppNotificationBlurCover.Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBlurCover.Opacity > .5,
            "exit transitions the text back into blur", ct);
        Assert.True(window.AppNotificationContent.Opacity < .5);
        Assert.True(window.AppNotificationBanner.Opacity > 0);
        await WpfFrameWaiter.UntilAsync(() => !window.IsAppNotificationBannerActive, "blurred notification finishes dismissal", ct);
        Assert.Null(window.AppNotificationBlurCover.Source);
        Assert.Equal(Visibility.Collapsed, window.AppNotificationBlurCover.Visibility);
        Assert.Equal(1, window.AppNotificationContent.Opacity);
    });

    [Fact]
    public void ReplacementAndImmediateCancellationCannotLeaveAnOldBlurCover() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        window.ShowAppNotification(Notification());
        var first = window.AppNotificationBlurCover.Source;
        window.DismissAppNotificationBanner();
        window.ShowAppNotification(new AppNotificationInfo { AppName = "Second app", Title = "Latest notification", Body = "Fresh content" });
        Assert.NotNull(window.AppNotificationBlurCover.Source);
        Assert.NotSame(first, window.AppNotificationBlurCover.Source);
        await WpfFrameWaiter.UntilAsync(() => window.AppNotificationBlurCover.Source == null && window.AppNotificationBanner.Opacity == 1,
            "replacement clears only its own blur cover", ct);
        Assert.True(window.IsAppNotificationBannerActive);
        Assert.Equal("Latest notification", window.AppNotificationTitleText.Text);
        window.DismissAppNotificationBanner();
        window.CancelAppNotificationBannerImmediate();
        Assert.Null(window.AppNotificationBlurCover.Source);
        Assert.Equal(0, window.AppNotificationBlurCover.Opacity);
        Assert.Equal(1, window.AppNotificationContent.Opacity);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledBlurAndReducedMotionUseTheCleanFade(bool reduceMotion, bool enableBlur) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(enableBlur);
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduceMotion);
        try
        {
            var window = fixture.Window;
            window.ShowAppNotification(Notification());
            Assert.Null(window.AppNotificationBlurCover.Source);
            Assert.Equal(Visibility.Collapsed, window.AppNotificationBlurCover.Visibility);
            Assert.Equal(1, window.AppNotificationContent.Opacity);
            window.DismissAppNotificationBanner();
            Assert.Null(window.AppNotificationBlurCover.Source);
            await WpfFrameWaiter.UntilAsync(() => !window.IsAppNotificationBannerActive, "clean fade finishes", ct);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    private static AppNotificationInfo Notification() => new()
        { AppName = "Test app", Title = "Đã xử lý xong ảnh", Body = "Người phụ trách đang phản hồi gypq." };
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(bool enableBlur = true) => new("vi", greeting: false,
        configureSettings: settings =>
        {
            settings.EnableLocalOnlyMode = true;
            settings.ShowSystemNotifications = true;
            settings.AutoDismissNotifications = false;
            settings.EnableBlurEffects = enableBlur;
        }, configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
}
