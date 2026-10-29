using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowLyricsPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ClipboardVisualTreeIsCreatedOnlyOnFirstUseAndSurvivesLocalization()
        => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Assert.Null(Field<object?>(window, "_clipboardTrayView"));
        Invoke(window, "RefreshNotchLocalization");
        Assert.Null(Field<object?>(window, "_clipboardTrayView"));
        var tray = window.ClipboardTrayView;
        Assert.Same(tray, window.ClipboardTrayView);
        Assert.Same(tray, window.ClipboardTrayHost.Content);
        Assert.Same(tray, window.FindName("ClipboardTrayView"));
    });

    [Fact]
    public void CollapsedTrackUpdatesDoNotQueueBlurInferenceLyricsOrSubtitles() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableLocalOnlyMode = false;
        settings.EnableSpotifyLyrics = settings.EnableOnlineLyrics = settings.EnableYouTubeSubtitles = true;
        settings.EnableBlurEffects = settings.ShowMediaArtBackground = settings.EnableSubjectBlur = true;
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        bitmap.Freeze();
        var info = new MediaInfo { CurrentTrack = "Track", CurrentArtist = "Artist", MediaSource = "Spotify", Thumbnail = bitmap, IsAnyMediaPlaying = true };
        for (int i = 0; i < 5; i++)
        {
            Invoke(window, "UpdateMediaBackground", info, false);
            ((Task)Invoke(window, "FetchLyricsForTrack", info)!).GetAwaiter().GetResult();
            info.MediaSource = "YouTube";
            info.YouTubeVideoId = "dQw4w9WgXcQ";
            ((Task)Invoke(window, "FetchSubtitlesForTrack", info, false)!).GetAwaiter().GetResult();
        }
        Assert.Null(Field<object?>(window, "_pendingBlurRequest"));
        Assert.False(Field<bool>(window, "_isBlurWorkerRunning"));
        Assert.Equal(0, Field<int>(window, "_blurTaskVersion"));
        Assert.Equal("", Field<string>(window, "_lyricsTrackKey"));
        Assert.Null(Field<string?>(window, "_subtitleRequestKey"));
        Assert.False((bool)typeof(MainWindow).GetProperty("IsSpotifyCanvasSurfaceVisible", Private)!.GetValue(window)!);
    });

    [Fact]
    public void NegativeSubtitleResultDoesNotRestartOnRepeatedMediaEvents() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableLocalOnlyMode = false;
        settings.EnableYouTubeSubtitles = true;
        Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator").ForceState(
            NotchView.Media, VNotch.Controllers.NotchShapeState.Expanded, "subtitle negative cache test");
        Set(window, "_subtitleRequestKey", "yt:dQw4w9WgXcQ");
        var info = new MediaInfo { CurrentTrack = "Track", MediaSource = "YouTube", YouTubeVideoId = "dQw4w9WgXcQ" };
        for (int i = 0; i < 5; i++)
            ((Task)Invoke(window, "FetchSubtitlesForTrack", info, false)!).GetAwaiter().GetResult();
        Assert.Equal("", Field<string>(window, "_lyricsTrackKey"));
        Assert.Equal(0, Field<int>(window, "_subtitleFetchFailureCount"));
    });

    [Fact]
    public void InactiveLyricsClearAStaleBackdropWhenWidgetReturns() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Field<NotchSettings>(window, "_settings").ExpandedWidget = "clock";
        Set(window, "_isLyricsActive", false);
        window.LyricsBlurBackground.Visibility = Visibility.Visible;
        window.LyricsBlurBackground.Opacity = 0.55;
        Invoke(window, "HideLyricsWidget");
        Assert.Equal(Visibility.Collapsed, window.LyricsBlurBackground.Visibility);
        Assert.Equal(0, window.LyricsBlurBackground.Opacity);
    });

    [Fact]
    public void ReturningWidgetCancelsBackdropFadeAndLateRefreshCannotRestoreIt() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.ExpandedWidget = "clock";
        settings.EnableBlurEffects = true;
        Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator").ForceState(
            VNotch.Models.NotchView.Media, VNotch.Controllers.NotchShapeState.Expanded, "lyrics test");
        Set(window, "_isLyricsActive", true);
        Invoke(window, "FadeInLyricsBlurBackgroundIfActive", true);
        Assert.Equal(Visibility.Visible, window.LyricsBlurBackground.Visibility);
        Invoke(window, "HideLyricsWidget");
        Assert.Equal(Visibility.Collapsed, window.LyricsBlurBackground.Visibility);
        Invoke(window, "FadeInLyricsBlurBackgroundIfActive", true);
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.CalendarWidget.Opacity - 1) < 1e-6, "returning widget", ct);
        Assert.Equal(Visibility.Collapsed, window.LyricsBlurBackground.Visibility);
        Assert.Equal(0, window.LyricsBlurBackground.Opacity);
        Assert.False(Field<bool>(window, "_isLyricsBlurFadeInProgress"));
    });

    [Fact]
    public void IgnoringDuplicateTrackEventsKeepsThePendingFetchGeneration() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableLocalOnlyMode = false;
        settings.EnableSpotifyLyrics = true;
        settings.EnableOnlineLyrics = true;
        var media = new MediaInfo { CurrentTrack = "Track", CurrentArtist = "Artist", MediaSource = "Spotify" };
        Set(window, "_lyricsTrackKey", "Track|Artist");
        Set(window, "_lyricsFetchGeneration", 42);
        Set(window, "_isLyricsActive", false);
        ((Task)Invoke(window, "FetchLyricsForTrack", media)!).GetAwaiter().GetResult();
        Assert.Equal(42, Field<int>(window, "_lyricsFetchGeneration"));
    });

    [Theory]
    [InlineData(false, "Artist")]
    [InlineData(true, "Artist")]
    [InlineData(false, "YouTube")]
    [InlineData(true, "")]
    public void IntroLyricsSeekingAndClearingKeepOnlyTheCorrectContentVisible(bool reducedMotion, string artist) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            var media = new MediaInfo { CurrentTrack = "Test song", CurrentArtist = artist, Duration = TimeSpan.FromSeconds(40) };
            Set(window, "_currentMediaInfo", media);
            var lines = new List<LyricLine>
            {
                new(TimeSpan.FromSeconds(5), "First line"),
                new(TimeSpan.FromSeconds(10), "Second line"),
                new(TimeSpan.FromSeconds(20), "Third line\ncontinued")
            };
            Invoke(window, "ShowLyricsSearchState", false);
            Assert.Equal(Loc.Get("lyrics.searching"), window.LyricsSearchText.Text);
            Invoke(window, "ApplySyncedLines", lines, media, " Test provider ");
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.LyricsPlaceholderPanel.Opacity - 1) < 1e-6, "lyrics intro placeholder", ct);
            Assert.Equal("Test song", window.LyricsPlaceholderTitle.Text);
            Assert.Equal(artist == "YouTube" ? "" : artist, window.LyricsPlaceholderArtist.Text);
            Assert.Contains("Test provider", window.LyricsPlaceholderProvider.Text);
            Assert.Equal(Visibility.Collapsed, window.LyricsSearchPanel.Visibility);

            foreach (var (seconds, expected) in new[] { (5, "First line"), (11, "Second line"), (25, "Third line\ncontinued"), (7, "First line") })
            {
                media.Position = TimeSpan.FromSeconds(seconds);
                Invoke(window, "UpdateLyricsDisplay");
                await WpfFrameWaiter.UntilAsync(() =>
                    (window.LyricTextA.Text == expected && Math.Abs(window.LyricTextA.Opacity - 1) < 1e-6 ||
                     window.LyricTextB.Text == expected && Math.Abs(window.LyricTextB.Opacity - 1) < 1e-6) &&
                    window.AnimatedLyricsLayer.OpacityMask == null, "the selected lyric line", ct);
                Assert.Equal(Visibility.Collapsed, window.LyricsPlaceholderPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.LyricsWidget.Visibility);
                Invoke(window, "UpdateLyricsDisplay");
            }

            media.Position = TimeSpan.Zero;
            Invoke(window, "UpdateLyricsDisplay");
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.LyricsPlaceholderPanel.Opacity - 1) < 1e-6, "intro after backward seek", ct);
            Assert.Equal(-1, Field<int>(window, "_currentLyricIndex"));
            Invoke(window, "UpdateLyricsDisplay");
            Invoke(window, "ApplySyncedLines", null, media, null);
            await WpfFrameWaiter.UntilAsync(() => window.LyricsWidget.Visibility == Visibility.Collapsed, "empty lyrics dismissal", ct);
            Assert.Null(Field<List<LyricLine>?>(window, "_currentLyrics"));
            Assert.Equal("", window.LyricTextA.Text);
            Assert.Equal("", window.LyricTextB.Text);
            Assert.Equal(Visibility.Visible, window.CalendarWidget.Visibility);
            Invoke(window, "ClearLyrics");
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSearchAndRapidLineTransitionsSettleOnTheLatestText(bool reducedMotion) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(reducedMotion);
            Invoke(window, "ShowLyricsSearchState", true);
            Assert.Equal(Loc.Get("subtitles.searching"), window.LyricsSearchText.Text);
            Invoke(window, "ShowLyricsSearchState", false);
            Assert.Equal(Loc.Get("lyrics.searching"), window.LyricsSearchText.Text);
            Invoke(window, "HideLyricsSearchState", false);
            Invoke(window, "ShowLyricsSearchState", true);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.LyricsSearchPanel.Opacity - 1) < 1e-6, "reopened lyrics search", ct);
            Assert.Equal(Visibility.Visible, window.LyricsSearchPanel.Visibility);
            Invoke(window, "AnimateLyricLine", "Old line", true, false);
            Invoke(window, "AnimateLyricLine", "Latest line", false, false);
            await WpfFrameWaiter.UntilAsync(() =>
                (window.LyricTextA.Text == "Latest line" && Math.Abs(window.LyricTextA.Opacity - 1) < 1e-6 ||
                 window.LyricTextB.Text == "Latest line" && Math.Abs(window.LyricTextB.Opacity - 1) < 1e-6) &&
                window.AnimatedLyricsLayer.OpacityMask == null, "latest lyric transition", ct);
            Assert.Equal(Visibility.Collapsed, window.LyricsSearchPanel.Visibility);
            Invoke(window, "ShowLyricsPlaceholder", "Instrumental", "Artist", "", false);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.LyricsPlaceholderPanel.Opacity - 1) < 1e-6, "instrumental placeholder", ct);
            Assert.Equal(Visibility.Collapsed, window.LyricsPlaceholderProvider.Visibility);
            Invoke(window, "ShowLyricsPlaceholder", "Instrumental", "Artist", "", false);
            Invoke(window, "HideLyricsPlaceholder", false);
            Invoke(window, "ShowLyricsPlaceholder", "New song", "New artist", "Provider", true);
            await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.LyricsPlaceholderPanel.Opacity - 1) < 1e-6, "replaced placeholder", ct);
            Assert.Equal("New song", window.LyricsPlaceholderTitle.Text);
            Assert.Equal("New artist", window.LyricsPlaceholderArtist.Text);
            Invoke(window, "HideLyricsPlaceholder", true);
            Assert.Equal(Visibility.Collapsed, window.LyricsPlaceholderPanel.Visibility);
            Invoke(window, "HideLyricsWidget");
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, -1)]
    [InlineData(5, 0)]
    [InlineData(9.999, 0)]
    [InlineData(10, 2)]
    [InlineData(20, 3)]
    [InlineData(100, 3)]
    public void TimestampLookupIncludesBoundariesAndUsesTheLastSimultaneousLine(double seconds, int expected) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Assert.Equal(-1, Invoke(window, "FindLyricIndex", TimeSpan.Zero));
        Set(window, "_currentLyrics", new List<LyricLine>
        {
            new(TimeSpan.FromSeconds(5), "First"),
            new(TimeSpan.FromSeconds(10), "Second"),
            new(TimeSpan.FromSeconds(10), "Simultaneous"),
            new(TimeSpan.FromSeconds(20), "Last")
        });
        Assert.Equal(expected, Invoke(window, "FindLyricIndex", TimeSpan.FromSeconds(seconds)));
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static object? Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
}
