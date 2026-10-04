using System.Reflection;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaWindowMatchingTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    [Theory]
    [InlineData("Spotify", "Spotify")]
    [InlineData("Discord", "DiscordCanary")]
    [InlineData("Twitch", "Twitch")]
    [InlineData("TIDAL", "TIDAL")]
    [InlineData("Deezer", "Deezer")]
    [InlineData("Apple Music", "AppleMusic")]
    public void NativeMediaPlatformsResolveTheirProcessCandidates(string source, string process)
    {
        var candidates = MediaWindowActivator.GetProcessCandidates(new MediaInfo { MediaSource = source }).ToArray();
        Assert.Contains(process, candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(candidates.Length, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("YouTube")]
    [InlineData("Browser")]
    [InlineData("SoundCloud")]
    [InlineData("Netflix")]
    public void BrowserMediaIncludesCommonBrowsersAndExplicitSessionExecutable(string source)
    {
        var candidates = MediaWindowActivator.GetProcessCandidates(new MediaInfo
        {
            MediaSource = source,
            SourceAppId = @"C:\Apps\custom-player.exe!chrome.exe"
        }).ToArray();
        Assert.Contains("custom-player", candidates);
        Assert.Contains("chrome", candidates);
        Assert.Contains("msedge", candidates);
        Assert.Contains("firefox", candidates);
    }

    [Fact]
    public void ExecutableAndPackagedAppNamesAreCaseInsensitiveAndDeduplicated()
    {
        var candidates = MediaWindowActivator.GetProcessCandidates(new MediaInfo
        { SourceAppId = "Spotify.exe!SPOTIFY.EXE!vesktop!applemusic!floorp" }).ToArray();
        Assert.Single(candidates.Where(name => name.Equals("spotify", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Vesktop", candidates);
        Assert.Contains("AppleMusic", candidates);
        Assert.Contains("floorp", candidates);
        Assert.Empty(MediaWindowActivator.GetProcessCandidates(new MediaInfo()));
    }

    [Theory]
    [InlineData("CHROME", true)]
    [InlineData("msedge", true)]
    [InlineData("helium", true)]
    [InlineData("supermium", true)]
    [InlineData("VNotch", false)]
    [InlineData("Spotify", false)]
    [InlineData("", false)]
    public void BrowserClassificationDoesNotTreatNativePlayersAsBrowsers(string process, bool expected) =>
        Assert.Equal(expected, MediaWindowActivator.IsBrowserProcess(process));

    [Fact]
    public void BrowserTabSearchHonorsAnExplicitBrowserOwnerButAllowsNativePlayerFallback()
    {
        Assert.True(Invoke<bool>("IsCandidateBrowserWindow", "chrome", new HashSet<string>()));
        Assert.False(Invoke<bool>("IsCandidateBrowserWindow", "Spotify", new HashSet<string>()));
        Assert.False(Invoke<bool>("IsCandidateBrowserWindow", "chrome", new HashSet<string> { "msedge" }));
        Assert.True(Invoke<bool>("IsCandidateBrowserWindow", "msedge", new HashSet<string> { "msedge" }));
        Assert.True(Invoke<bool>("IsCandidateBrowserWindow", "chrome", new HashSet<string> { "Spotify" }));
    }

    [Theory]
    [InlineData("YouTube", "youtube")]
    [InlineData("Twitch", "twitch")]
    [InlineData("Discord", "vesktop")]
    [InlineData("SoundCloud", "soundcloud")]
    [InlineData("Spotify", "spotify")]
    [InlineData("TIDAL", "tidal")]
    [InlineData("Deezer", "deezer")]
    [InlineData("Bandcamp", "bandcamp")]
    [InlineData("Netflix", "netflix")]
    [InlineData("Bilibili", "哔哩哔哩")]
    [InlineData("Vimeo", "vimeo")]
    [InlineData("Instagram", "instagram")]
    [InlineData("Twitter", " / x")]
    public void PlatformLabelsHelpMatchTabsWithoutOverridingExactTrackMatches(string source, string label)
    {
        var info = new MediaInfo { MediaSource = source };
        Assert.Equal(50, Invoke<int>("ScoreTabItem", label, "", info));
        Assert.Equal(0, Invoke<int>("ScoreTabItem", "unrelated tab", "", info));
        if (source != "Spotify") Assert.Equal(90, Invoke<int>("ScoreWindowPlatform", label, info.Platform));
    }

    [Fact]
    public void ExactVideoIdentityOutranksMatchingTrackArtistAndPlatform()
    {
        var info = new MediaInfo
        {
            MediaSource = "YouTube",
            CurrentTrack = "Fixture Song",
            CurrentArtist = "Fixture Artist",
            YouTubeTitle = "Fixture Video",
            YouTubeVideoId = "aBc123XYZ90"
        };
        Assert.Equal(610, Invoke<int>("ScoreTabItem", "fixture song fixture artist fixture video youtube", "https://youtube.com/watch?v=abc123xyz90", info));
        Assert.Equal(200, Invoke<int>("ScoreTabItem", "other", "https://example.org/abc123xyz90", info));
        Assert.Equal(0, Invoke<int>("ScoreTabItem", "", "", info));
        info.CurrentArtist = "YouTube";
        info.CurrentTrack = "ab";
        info.YouTubeTitle = "cd";
        info.YouTubeVideoId = null;
        Assert.Equal(50, Invoke<int>("ScoreTabItem", "ab cd youtube", "", info));
    }

    [Fact]
    public void WindowMatchingNormalizesBrowserSuffixesAndKeepsTrackArtistAndPlatformSignals()
    {
        var info = new MediaInfo { MediaSource = "YouTube", CurrentTrack = "Fixture  Song - YouTube", CurrentArtist = "Fixture Artist" };
        Assert.Equal(405, Invoke<int>("ScoreWindow", "Fixture Song Fixture Artist - YouTube - Google Chrome", "chrome", info));
        Assert.Equal(380, Invoke<int>("ScoreWindow", "Fixture Song Fixture Artist - YouTube - Google Chrome", "native", info));
        Assert.Equal("song title", Invoke<string>("NormalizeTitle", "  SONG\t TITLE  - Microsoft Edge"));
        Assert.Equal("", Invoke<string>("NormalizeTitle", (object?)null));
        Assert.Equal("", Invoke<string>("NormalizeTitle", " \t "));
        Assert.Equal(0, Invoke<int>("ScoreWindow", "unrelated", "native", new MediaInfo()));
        Assert.False(MediaWindowActivator.TryActivateWindow(IntPtr.Zero));
        Assert.False(MediaWindowActivator.TryGoBackInMediaTab(new MediaInfo { CurrentTrack = "ab" }));
    }

    private static T Invoke<T>(string method, params object?[] arguments) =>
        (T)typeof(MediaWindowActivator).GetMethod(method, Private)!.Invoke(null, arguments)!;
}
