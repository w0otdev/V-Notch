using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    public void SpotifyPrefetchWarmsTheSelectedSourceWithoutChangingCollapsedUi() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableLocalOnlyMode = false;
        settings.EnableOnlineLyrics = settings.EnableSpotifyLyrics = true;
        int requests = 0;
        using var client = new System.Net.Http.HttpClient(new LyricsHandler(() => requests++))
        { BaseAddress = new Uri("https://lyrics.test") };
        var local = new SpotifyLocalMetadataReader(() => new("Track", "Artist", "2dkIbR4mudsHg45av3YwxQ"));
        var service = new LyricsService(client, client, netEaseClient: client, kugouClient: client, qqMusicClient: client, amllClient: client, spotifyLocalMetadata: local);
        Field<LyricsService>(window, "_lyricsService").Dispose();
        Set(window, "_lyricsService", service);
        var info = new MediaInfo
        {
            CurrentTrack = "Track",
            CurrentArtist = "Artist",
            Duration = TimeSpan.FromSeconds(240),
            MediaSource = "Spotify",
            IsPlaying = true
        };
        Invoke(window, "PrefetchSpotifyLyrics", info);
        Invoke(window, "PrefetchSpotifyLyrics", info);
        var lyrics = service.FetchSyncedLyricsAsync("Track", "Artist", 240, settings.LyricsProviderPriority).GetAwaiter().GetResult();
        Assert.NotNull(lyrics);
        Assert.Equal(LyricsProviderOrder.Amll, lyrics.Provider);
        Assert.Equal(1, requests);
        Assert.Equal("", Field<string>(window, "_lyricsTrackKey"));
        Assert.False(Field<bool>(window, "_isLyricsSearchVisible"));
    });

    private sealed class LyricsHandler(Action request) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage message, CancellationToken token)
        {
            request();
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><p begin=\"1s\" end=\"2s\"><span begin=\"1s\" end=\"2s\">Selected source</span></p></body></tt>") });
        }
    }

    [Fact]
    public void LineLyricsRemainVisibleDuringBackgroundWordSearchThenUpgrade() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableLocalOnlyMode = false;
        settings.EnableOnlineLyrics = settings.EnableSpotifyLyrics = true;
        settings.LyricsProviderPriority = "LRCLIB,lrc mux";
        var response = new TaskCompletionSource<System.Net.Http.HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var line = new System.Net.Http.HttpClient(new ProgressiveHandler(_ => Task.FromResult(
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{\"syncedLyrics\":\"[00:01.00]Hello world\"}") })))
        { BaseAddress = new Uri("https://line.test") };
        using var words = new System.Net.Http.HttpClient(new ProgressiveHandler(token => response.Task.WaitAsync(token)))
        { BaseAddress = new Uri("https://words.test") };
        var service = new LyricsService(line, words);
        Field<LyricsService>(window, "_lyricsService").Dispose();
        Set(window, "_lyricsService", service);
        var info = new MediaInfo
        {
            CurrentTrack = "Track",
            CurrentArtist = "Artist",
            MediaSource = "Spotify",
            Duration = TimeSpan.FromSeconds(240),
            Position = TimeSpan.FromSeconds(1)
        };
        Set(window, "_currentMediaInfo", info);
        Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator").ForceState(
            NotchView.Media, VNotch.Controllers.NotchShapeState.Expanded, "progressive lyrics test");
        window.ExpandedContent.Visibility = Visibility.Visible;
        var pending = (Task)Invoke(window, "FetchLyricsForTrack", info)!;
        await WpfFrameWaiter.UntilAsync(() => Field<List<LyricLine>?>(window, "_currentLyrics") is { Count: > 0 }, "line lyrics preview", ct);
        Assert.False(pending.IsCompleted);
        Assert.False(Field<bool>(window, "_isLyricsSearchVisible"));
        Assert.Null(Field<List<LyricLine>>(window, "_currentLyrics")[0].Words);
        response.SetResult(new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(ProgressiveLyricsTests.WordPayload) });
        await pending.WaitAsync(ct);
        Assert.NotNull(Field<List<LyricLine>>(window, "_currentLyrics")[0].Words);
    });

    private sealed class ProgressiveHandler(Func<CancellationToken, Task<System.Net.Http.HttpResponseMessage>> response) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token) => response(token);
    }

    [Fact]
    public void BrowserTrackRecognizedAsSpotifySwitchesFromSubtitlesToLyricsAndCanvas() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableLocalOnlyMode = false;
        settings.EnableOnlineLyrics = settings.EnableSpotifyLyrics = settings.EnableYouTubeSubtitles = true;
        settings.LyricsProviderPriority = "LRCLIB";
        using var client = new System.Net.Http.HttpClient(new ProgressiveHandler(_ => Task.FromResult(
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{\"syncedLyrics\":\"[00:01.00]Browser lyrics\"}") })))
        { BaseAddress = new Uri("https://lyrics.test") };
        Field<LyricsService>(window, "_lyricsService").Dispose();
        Set(window, "_lyricsService", new LyricsService(client, client));
        Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator").ForceState(
            NotchView.Media, VNotch.Controllers.NotchShapeState.Expanded, "Spotify Web test");
        window.ExpandedContent.Visibility = Visibility.Visible;
        var info = new MediaInfo
        {
            CurrentTrack = "Song", CurrentArtist = "Artist", SourceAppId = "Chrome", MediaSource = "Browser",
            Duration = TimeSpan.FromMinutes(3), IsPlaying = true, IsAnyMediaPlaying = true
        };
        Invoke(window, "ApplyMediaUpdate", info);
        Assert.NotNull(Field<string?>(window, "_subtitleRequestKey"));
        var spotify = info.Clone();
        spotify.MediaSource = "Spotify";
        Invoke(window, "ApplyMediaUpdate", spotify);
        await WpfFrameWaiter.UntilAsync(() => Field<List<LyricLine>?>(window, "_currentLyrics") is { Count: > 0 },
            "Spotify Web lyrics", ct);
        Assert.Equal("Browser lyrics", Field<List<LyricLine>>(window, "_currentLyrics")[0].Text);
        Assert.Equal("Song|Artist", Field<VNotch.Controllers.SpotifyCanvasController>(window, "_spotifyCanvasController").CurrentTrackKey);
        Assert.Null(Field<string?>(window, "_subtitleRequestKey"));
    });

    [Fact]
    public void LyricsUseThePlaybackClockWhenMetadataIsStaleOrStillSaysPlaying() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Set(window, "_currentMediaInfo", new MediaInfo
        {
            Position = TimeSpan.FromSeconds(5),
            Duration = TimeSpan.FromMinutes(3),
            IsPlaying = true,
            LastUpdated = DateTimeOffset.UtcNow.AddSeconds(-20)
        });
        var engine = window.MediaPlayback.Progress;
        engine.OnMediaSnapshot(new ProgressSnapshot
        {
            Position = TimeSpan.FromSeconds(40),
            Duration = TimeSpan.FromMinutes(3),
            IsPlaying = false,
            PlaybackRate = 1,
            Timestamp = DateTime.UtcNow,
            SequenceNumber = 1
        });
        var playback = ((TimeSpan Position, bool Playing))Invoke(window, "GetLyricsPlayback")!;
        Assert.Equal(TimeSpan.FromSeconds(40), playback.Position);
        Assert.False(playback.Playing);
    });

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
    public void CompactCollapseAndReopeningTheSameTrackReuseMediaAndLyricsBackdrops() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.EnableBlurEffects = settings.ShowMediaArtBackground = true;
        settings.NotchStyle = "default";
        settings.BrightenDarkLyricsBackground = false;
        window.SetDebugViewLock(true);
        window.Show();
        var coordinator = Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator");
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !coordinator.IsTransitionActive, "backdrop window startup", ct);
        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "backdrop media view", ct);

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        bitmap.Freeze();
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = "Track", CurrentArtist = "Artist", Thumbnail = bitmap, IsAnyMediaPlaying = true });
        Set(window, "_lastBlurThumbnailRef", bitmap);
        Set(window, "_lastBackgroundThumbnail", bitmap);
        Set(window, "_isLyricsActive", true);
        window.MediaBackgroundImage.Source = window.MediaBackgroundImage2.Source = bitmap;
        window.MediaBackgroundImage.Opacity = window.MediaBackgroundImage2.Opacity = 1;
        window.LyricsBlurImage.Source = bitmap;
        int blurVersion = Field<int>(window, "_blurTaskVersion");

        window.SetDebugViewState("CollapsedNotch");
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "backdrop compact collapse", ct);
        Assert.Same(bitmap, window.MediaBackgroundImage.Source);
        Assert.Same(bitmap, window.LyricsBlurImage.Source);

        // Both a delayed hide completion and a repeated compact update retain artwork.
        Invoke(window, "HideMediaBackgroundOverlay");
        await WpfFrameWaiter.UntilAsync(() => window.MediaBackground.Visibility == Visibility.Collapsed, "backdrop overlay hide", ct);
        Invoke(window, "HideMediaBackgroundOverlay");
        Invoke(window, "FadeInLyricsBlurBackgroundIfActive", false);
        Assert.Same(bitmap, window.MediaBackgroundImage.Source);
        Assert.Same(bitmap, window.LyricsBlurImage.Source);
        Assert.Same(bitmap, Field<object>(window, "_lastBlurThumbnailRef"));

        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "backdrop same-track reopen", ct);
        Invoke(window, "FadeInLyricsBlurBackgroundIfActive", false);
        Assert.Same(bitmap, window.MediaBackgroundImage.Source);
        Assert.Same(bitmap, window.LyricsBlurImage.Source);
        Assert.Equal(Visibility.Visible, window.LyricsBlurBackground.Visibility);
        Assert.Equal(blurVersion, Field<int>(window, "_blurTaskVersion"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactBackdropsReleaseArtworkWhenMediaStops(bool closedSession) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Set(window, "_currentMediaInfo", closedSession ? null : new MediaInfo { IsAnyMediaPlaying = false });
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4);
        bitmap.Freeze();
        window.MediaBackgroundImage.Source = window.MediaBackgroundImage2.Source = bitmap;
        window.MediaBackgroundImageBack.Source = window.MediaBackgroundImageBack2.Source = bitmap;
        window.MediaBackground.Opacity = 0;
        window.MediaBackground.Visibility = Visibility.Collapsed;
        window.LyricsBlurImage.Source = window.LyricsBlurImageNext.Source = bitmap;
        Set(window, "_lastBlurThumbnailRef", bitmap);

        Invoke(window, "HideMediaBackgroundOverlay");
        Invoke(window, "HideLyricsBlurBackgroundImmediately");

        Assert.Null(window.MediaBackgroundImage.Source);
        Assert.Null(window.MediaBackgroundImage2.Source);
        Assert.Null(window.MediaBackgroundImageBack.Source);
        Assert.Null(window.MediaBackgroundImageBack2.Source);
        Assert.Null(window.LyricsBlurImage.Source);
        Assert.Null(window.LyricsBlurImageNext.Source);
        Assert.Null(Field<object?>(window, "_lastBlurThumbnailRef"));
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
    [InlineData(0.25, false, false)]
    [InlineData(0.75, false, false)]
    [InlineData(0.25, true, false)]
    [InlineData(0.75, true, false)]
    [InlineData(0.25, false, true)]
    [InlineData(0.75, false, true)]
    [InlineData(0.25, true, true)]
    [InlineData(0.75, true, true)]
    public void ChangingLinesDuringEntranceKeepsTheOutgoingLineAtItsVisiblePosition(
        double opacity, bool toPlaceholder, bool wordTiming) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        bool previous = AnimationConfig.ReduceMotion;
        try
        {
            AnimationConfig.SetReduceMotion(false);
            Invoke(window, "ShowLyricsWidget");
            if (wordTiming)
            {
                Set(window, "_currentLyrics", new List<LyricLine>
                {
                    new(TimeSpan.Zero, "First line", new[] { new LyricWord(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 10) })
                });
                Set(window, "_currentLyricIndex", 0);
            }
            Invoke(window, "AnimateLyricLine", "First line", false, false);
            // Seek real WPF animation clocks to a reproducible entrance frame.
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1));
            var slide = new DoubleAnimation(14, 0, TimeSpan.FromSeconds(1));
            var fadeClock = (AnimationClock)fade.CreateClock(true);
            var slideClock = (AnimationClock)slide.CreateClock(true);
            window.LyricTextA.ApplyAnimationClock(UIElement.OpacityProperty, fadeClock);
            window.LyricTranslateA.ApplyAnimationClock(TranslateTransform.YProperty, slideClock);
            fadeClock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(opacity), TimeSeekOrigin.BeginTime);
            slideClock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(opacity), TimeSeekOrigin.BeginTime);
            double y = window.LyricTranslateA.Y;

            if (toPlaceholder)
                Invoke(window, "ShowLyricsPlaceholder", "Instrumental", "Artist", "", false);
            else
                Invoke(window, "AnimateLyricLine", "Second line", false, false);

            Assert.Equal("First line", window.LyricTextA.Text);
            Assert.Equal(y, window.LyricTranslateA.Y, 3);
            Assert.Equal(opacity, window.LyricTextA.Opacity, 3);
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

    [Fact]
    public void NoWidgetModeShrinksMediaViewToTitleAndExpandsWhenLyricsAreActive() => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        var shellState = Field<NotchShellState>(window, "_shellState");
        var coordinator = Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator");

        window.SetDebugViewLock(true);
        window.Show();
        await WpfFrameWaiter.UntilAsync(() => Field<bool>(window, "_isStartupLayoutReady") && !coordinator.IsTransitionActive, "no-widget startup", ct);

        window.SetDebugViewState("MediaExpanded");
        await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "no-widget media expand", ct);
        Assert.Equal(480, shellState.ExpandedWidth);

        settings.ExpandedWidget = "none";
        Invoke(window, "ApplyExpandedWidgetMode", true);
        Assert.Equal(328, shellState.ExpandedWidth);
        await WpfFrameWaiter.UntilAsync(() =>
            !Field<bool>(window, "_isMediaWidthAnimating") &&
            Math.Abs(window.NotchBorder.ActualWidth - 328) < 0.5 &&
            Math.Abs(window.ExpandedContent.ActualWidth - 312) < 0.5,
            "no-widget media shrink", ct);

        Invoke(window, "ShowLyricsWidget");
        Assert.Equal(480, shellState.ExpandedWidth);
        await WpfFrameWaiter.UntilAsync(() =>
            !Field<bool>(window, "_isMediaWidthAnimating") &&
            Math.Abs(window.NotchBorder.ActualWidth - 480) < 0.5 &&
            Math.Abs(window.ExpandedContent.ActualWidth - 464) < 0.5,
            "no-widget lyrics expand", ct);

        Invoke(window, "HideLyricsWidget");
        Assert.Equal(328, shellState.ExpandedWidth);
        await WpfFrameWaiter.UntilAsync(() =>
            !Field<bool>(window, "_isMediaWidthAnimating") &&
            Math.Abs(window.NotchBorder.ActualWidth - 328) < 0.5 &&
            Math.Abs(window.ExpandedContent.ActualWidth - 312) < 0.5 &&
            window.LyricsBlurBackground.Visibility == Visibility.Collapsed,
            "no-widget lyrics hide shrink", ct);
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        settings => { settings.EnableLocalOnlyMode = true; settings.DisableMouseLeaveAutoClose = true; },
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static object? Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, arguments);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
}
