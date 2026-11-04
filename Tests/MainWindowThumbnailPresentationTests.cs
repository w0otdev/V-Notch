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

    [Fact]
    public void ArtworkSnapshotCannotReplaceTheFullSpotifyArtistList() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(false);
        var window = fixture.Window;
        window.SetDebugViewLock(true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !Property<bool>(window, "_isAnimating"), "artist window startup", ct);
        var info = new MediaInfo
        {
            CurrentTrack = "Song",
            CurrentArtist = "Singer, Collaborator",
            MediaSource = "Spotify",
            SourceAppId = "Spotify.exe",
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            Thumbnail = Bitmap("white-mat-artwork.png")
        };
        Invoke(window, "ApplyMediaUpdate", info);
        var marquee = window.MediaPlayback.GetType().GetField("_marqueeController", Private)!.GetValue(window.MediaPlayback)!;
        string ArtistText() => (string)marquee.GetType().GetField("_lastArtistText", Private)!.GetValue(marquee)!;
        const string expected = "Singer, Collaborator";
        Assert.Equal(expected, ArtistText());

        var artwork = info.Clone();
        artwork.CurrentArtist = "Singer";
        artwork.IsThumbnailOnlyUpdate = true;
        artwork.Thumbnail = Bitmap("dark-textured-artwork.png");
        Invoke(window, "ApplyMediaUpdate", artwork);
        Assert.Equal(expected, ArtistText());
        Assert.Null(marquee.GetType().GetField("_pendingArtistText", Private)!.GetValue(marquee));
    });

    [Theory]
    [InlineData("SoundCloud")]
    [InlineData("Browser")]
    public void NextSoundCloudTrackKeepsOutgoingVisualUntilNewArtworkArrives(string source) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(true);
        var window = fixture.Window;
        window.SetDebugViewLock(true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !Property<bool>(window, "_isAnimating"), "window startup", ct);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !Property<bool>(window, "_isAnimating"), "media layout", ct);
        var old = new MediaInfo
        {
            CurrentTrack = "First",
            CurrentArtist = "Artist",
            MediaSource = source,
            SourceAppId = "Helium",
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            Thumbnail = Bitmap("white-mat-artwork.png")
        };
        Invoke(window, "ApplyMediaUpdate", old);
        var next = old.Clone();
        next.CurrentTrack = "Next";
        next.Thumbnail = null;
        Invoke(window, "ApplyMediaUpdate", next);
        Assert.Same(old.Thumbnail, window.ThumbnailImage.Source);
        Assert.Same(old.Thumbnail, window.CompactThumbnail.Source);
        Assert.Null(window.ThumbnailImageNext.Source);
        Assert.Null(window.CompactThumbnailNext.Source);
        Assert.Equal(Visibility.Collapsed, window.ThumbnailFallback.Visibility);
        Assert.Null(next.Thumbnail);
        // Repeated missing-art snapshots and rapid next must not flash the empty icon.
        Invoke(window, "ApplyMediaUpdate", next.Clone());
        next.CurrentTrack = "Rapid next";
        Invoke(window, "ApplyMediaUpdate", next.Clone());
        Assert.Same(old.Thumbnail, window.ThumbnailImage.Source);
        Assert.Equal(Visibility.Collapsed, window.ThumbnailFallback.Visibility);
        Assert.Null(Field<MediaInfo>(window, "_currentMediaInfo").Thumbnail);
        next = next.Clone();
        next.Thumbnail = Bitmap("dark-textured-artwork.png");
        Invoke(window, "ApplyMediaUpdate", next);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isThumbnailSwitchActive"), "delayed cover reveal", ct);
        Assert.Same(next.Thumbnail, window.ThumbnailImage.Source);
        Assert.Same(next.Thumbnail, window.CompactThumbnail.Source);
        var corrected = next.Clone();
        corrected.Thumbnail = Bitmap("white-mat-artwork.png");
        Invoke(window, "ApplyMediaUpdate", corrected);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isThumbnailSwitchActive"), "corrected session cover", ct);
        Assert.Same(corrected.Thumbnail, window.ThumbnailImage.Source);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThumbnailMorphCoalescesLatestImageAndCancellationRestoresBothLayers(bool blurEnabled) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(blurEnabled);
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
        await using var fixture = CreateFixture(true);
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
        await using var fixture = CreateFixture(true);
        var window = fixture.Window;
        window.CompactThumbnail.Source = Bitmap("white-mat-artwork.png");
        window.CompactThumbnailBorder.Visibility = Visibility.Visible;
        Invoke(window, "PlayThumbnailRevealAnimation");
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.CompactThumbnailScale.ScaleX - 1) < 1e-6 && Math.Abs(window.CompactThumbnailBorder.Opacity - 1) < 1e-6, "compact thumbnail reveal", ct);
        int exits = 0;
        Invoke(window, "PlayCompactThumbnailExitAnimation", (Action)(() => exits++));
        await WpfFrameWaiter.UntilAsync(() => exits == 1, "compact thumbnail exit", ct);
        Assert.Equal(Visibility.Collapsed, window.CompactThumbnailBorder.Visibility);
        window.CompactThumbnailBorder.Visibility = Visibility.Visible;
        Invoke(window, "PlayCompactThumbnailExitAnimation", (Action)(() => exits++));
        Invoke(window, "PlayThumbnailRevealAnimation");
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.CompactThumbnailScale.ScaleX - 1) < 1e-6 && Math.Abs(window.CompactThumbnailBorder.Opacity - 1) < 1e-6, "compact reveal supersedes exit", ct);
        Assert.Equal(1, exits);
        Assert.Equal(Visibility.Visible, window.CompactThumbnailBorder.Visibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdExpandTargetMatchesLiveExpandedThumbnailSlot(bool islandMode) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture(true);
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableDynamicIslandMode = islandMode;
        window.SetDebugViewLock(true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !Property<bool>(window, "_isAnimating"), "expand target startup", ct);

        Set(window, "_cachedThumbnailExpandTarget", null!);
        Invoke(window, "SetupExpandedContentBeforeAnimation");
        var coldTarget = ((double X, double Y)?)InvokeResult(window, "EnsureCachedThumbnailExpandTarget");
        Assert.True(coldTarget.HasValue);

        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !Property<bool>(window, "_isAnimating"), "media expanded layout", ct);
        window.UpdateLayout();

        var parameters = new object?[] { default((double X, double Y)) };
        bool hasLive = (bool)typeof(MainWindow).GetMethod("TryComputeThumbnailExpandTarget", Private)!.Invoke(window, parameters)!;
        Assert.True(hasLive);
        var liveTarget = ((double X, double Y))parameters[0]!;

        Assert.Equal(liveTarget.X, coldTarget.Value.X, 3);
        Assert.Equal(liveTarget.Y, coldTarget.Value.Y, 3);
        Assert.False(window.ThumbnailBorder.UseLayoutRounding);
        Assert.False(window.AnimationThumbnailBorder.UseLayoutRounding);
        Assert.False(window.CompactThumbnailBorder.UseLayoutRounding);

        settings.EnableDynamicIslandMode = !islandMode;
        var toggledTarget = ((double X, double Y)?)InvokeResult(window, "EnsureCachedThumbnailExpandTarget");
        Assert.True(toggledTarget.HasValue);
        Assert.NotEqual(coldTarget.Value.Y, toggledTarget.Value.Y);
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
    private static void Invoke(MainWindow window, string name, params object?[] args) => ReflectionInvoker.InvokeWithDefaults(window, typeof(MainWindow).GetMethod(name, Private)!, args);
    private static object? InvokeResult(MainWindow window, string name, params object?[] args) => ReflectionInvoker.InvokeWithDefaults(window, typeof(MainWindow).GetMethod(name, Private)!, args);
}
