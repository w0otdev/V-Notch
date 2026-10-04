using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowThumbnailPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThumbnailMorphCoalescesLatestImageAndCancellationRestoresBothLayers(bool blurEnabled) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(blurEnabled);
        var window = fixture.Window;
        var first = Bitmap("white-mat-artwork.png");
        var next = Bitmap("dark-textured-artwork.png");
        var latest = Bitmap("white-mat-artwork.png");
        window.ThumbnailImage.Source = first;
        window.CompactThumbnail.Source = first;
        Invoke(window, "AnimateThumbnailSwitchOnly", next, true);
        Assert.True(Field<bool>(window, "_isThumbnailSwitchActive"));
        Assert.Same(next, window.ThumbnailImageNext.Source);
        Invoke(window, "AnimateThumbnailSwitchOnly", latest, false);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isThumbnailSwitchActive"), "coalesced thumbnail morph", ct);
        Assert.Same(latest, window.ThumbnailImage.Source);
        Assert.Same(latest, window.CompactThumbnail.Source);
        Assert.Null(window.ThumbnailImageNext.Source);
        Assert.Equal(0, window.ThumbnailOutBlur.Radius);
        Assert.Equal(1, window.ThumbnailOutScale.ScaleX);
        Invoke(window, "AnimateThumbnailSwitchOnly", next, true);
        await WpfFrameWaiter.NextAsync(ct);
        Invoke(window, "CancelThumbnailSwitchAnimations", first);
        Assert.False(Field<bool>(window, "_isThumbnailSwitchActive"));
        Assert.Same(first, window.ThumbnailImage.Source);
        Assert.Same(first, window.CompactThumbnail.Source);
        Assert.Equal(Visibility.Collapsed, window.ThumbnailImageNext.Visibility);
        Invoke(window, "AnimateThumbnailSwitchOnly", next, true);
        Invoke(window, "CancelThumbnailSwitchForExpand");
        Assert.Same(next, window.ThumbnailImage.Source);
        Assert.False(Field<bool>(window, "_isThumbnailSwitchActive"));
        Invoke(window, "ResetCompactThumbnailNextLayer");
        Assert.Null(window.CompactThumbnailNext.Source);
        Invoke(window, "AnimateThumbnailSwitchOnly", next, false);
        Assert.False(Field<bool>(window, "_isThumbnailSwitchActive"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingTrackArtworkShowsFallbackAndClearsPendingImages(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(true);
        var window = fixture.Window;
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            window.SetDebugViewLock(true);
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !Property<bool>(window, "_isAnimating"), "thumbnail window startup", ct);
            window.SetDebugViewState("MediaExpanded");
            await WpfFrameWaiter.UntilAsync(() => !Property<bool>(window, "_isAnimating"), "thumbnail media view", ct);
            window.ThumbnailImage.Source = Bitmap("white-mat-artwork.png");
            window.ThumbnailImage.Visibility = Visibility.Visible;
            Set(window, "_pendingFlipThumbnail", Bitmap("dark-textured-artwork.png"));
            Invoke(window, "TransitionToEmptyThumbnail");
            Invoke(window, "TransitionToEmptyThumbnail");
            await WpfFrameWaiter.UntilAsync(() => window.ThumbnailImage.Source == null, "empty thumbnail transition", ct);
            Assert.Equal(Visibility.Visible, window.ThumbnailFallback.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ThumbnailImage.Visibility);
            Assert.Null(window.ThumbnailImageNext.Source);
            Assert.Null(Field<ImageSource?>(window, "_pendingFlipThumbnail"));
            Assert.Equal(0, window.ThumbnailOutBlur.Radius);
            Assert.Equal(1, window.ThumbnailOutScale.ScaleX);
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void CompactThumbnailRevealExitAndInterruptedExitKeepLatestState() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(true);
        var window = fixture.Window;
        window.CompactThumbnail.Source = Bitmap("white-mat-artwork.png");
        window.CompactThumbnailBorder.Visibility = Visibility.Visible;
        Invoke(window, "PlayThumbnailRevealAnimation");
        await WpfFrameWaiter.UntilAsync(() => window.CompactThumbnailScale.ScaleX == 1 && window.CompactThumbnailBorder.Opacity == 1, "compact thumbnail reveal", ct);
        int exits = 0;
        Invoke(window, "PlayCompactThumbnailExitAnimation", (Action)(() => exits++));
        await WpfFrameWaiter.UntilAsync(() => exits == 1, "compact thumbnail exit", ct);
        Assert.Equal(Visibility.Collapsed, window.CompactThumbnailBorder.Visibility);
        window.CompactThumbnailBorder.Visibility = Visibility.Visible;
        Invoke(window, "PlayCompactThumbnailExitAnimation", (Action)(() => exits++));
        Invoke(window, "PlayThumbnailRevealAnimation");
        await WpfFrameWaiter.UntilAsync(() => window.CompactThumbnailScale.ScaleX == 1 && window.CompactThumbnailBorder.Opacity == 1, "compact reveal supersedes exit", ct);
        Assert.Equal(1, exits);
        Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
    });

    private static BitmapImage Bitmap(string name)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(bool blur) => new("en", greeting: false,
        configureSettings: settings => { settings.EnableLocalOnlyMode = true; settings.EnableBlurEffects = blur; settings.DisableMouseLeaveAutoClose = true; },
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static T Property<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
