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

    [Theory]
    [InlineData(false, "Artist")]
    [InlineData(true, "Artist")]
    [InlineData(false, "YouTube")]
    [InlineData(true, "")]
    public void IntroLyricsSeekingAndClearingKeepOnlyTheCorrectContentVisible(bool reducedMotion, string artist) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
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
            await WpfFrameWaiter.UntilAsync(() => window.LyricsPlaceholderPanel.Opacity == 1, "lyrics intro placeholder", ct);
            Assert.Equal("Test song", window.LyricsPlaceholderTitle.Text);
            Assert.Equal(artist == "YouTube" ? "" : artist, window.LyricsPlaceholderArtist.Text);
            Assert.Contains("Test provider", window.LyricsPlaceholderProvider.Text);
            Assert.Equal(Visibility.Collapsed, window.LyricsSearchPanel.Visibility);

            foreach (var (seconds, expected) in new[] { (5, "First line"), (11, "Second line"), (25, "Third line\ncontinued"), (7, "First line") })
            {
                media.Position = TimeSpan.FromSeconds(seconds);
                Invoke(window, "UpdateLyricsDisplay");
                await WpfFrameWaiter.UntilAsync(() =>
                    (window.LyricTextA.Text == expected && window.LyricTextA.Opacity == 1 ||
                     window.LyricTextB.Text == expected && window.LyricTextB.Opacity == 1) &&
                    window.AnimatedLyricsLayer.OpacityMask == null, "the selected lyric line", ct);
                Assert.Equal(Visibility.Collapsed, window.LyricsPlaceholderPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.LyricsWidget.Visibility);
                Invoke(window, "UpdateLyricsDisplay");
            }

            media.Position = TimeSpan.Zero;
            Invoke(window, "UpdateLyricsDisplay");
            await WpfFrameWaiter.UntilAsync(() => window.LyricsPlaceholderPanel.Opacity == 1, "intro after backward seek", ct);
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
        using var fixture = CreateFixture();
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
            await WpfFrameWaiter.UntilAsync(() => window.LyricsSearchPanel.Opacity == 1, "reopened lyrics search", ct);
            Assert.Equal(Visibility.Visible, window.LyricsSearchPanel.Visibility);
            Invoke(window, "AnimateLyricLine", "Old line", true, false);
            Invoke(window, "AnimateLyricLine", "Latest line", false, false);
            await WpfFrameWaiter.UntilAsync(() =>
                (window.LyricTextA.Text == "Latest line" && window.LyricTextA.Opacity == 1 ||
                 window.LyricTextB.Text == "Latest line" && window.LyricTextB.Opacity == 1) &&
                window.AnimatedLyricsLayer.OpacityMask == null, "latest lyric transition", ct);
            Assert.Equal(Visibility.Collapsed, window.LyricsSearchPanel.Visibility);
            Invoke(window, "ShowLyricsPlaceholder", "Instrumental", "Artist", "", false);
            await WpfFrameWaiter.UntilAsync(() => window.LyricsPlaceholderPanel.Opacity == 1, "instrumental placeholder", ct);
            Assert.Equal(Visibility.Collapsed, window.LyricsPlaceholderProvider.Visibility);
            Invoke(window, "ShowLyricsPlaceholder", "Instrumental", "Artist", "", false);
            Invoke(window, "HideLyricsPlaceholder", false);
            Invoke(window, "ShowLyricsPlaceholder", "New song", "New artist", "Provider", true);
            await WpfFrameWaiter.UntilAsync(() => window.LyricsPlaceholderPanel.Opacity == 1, "replaced placeholder", ct);
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
