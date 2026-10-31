using System.Globalization;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PlatformDetectorTests
{
    #region DetectFromWindowTitles

    [Fact]
    public void DetectFromWindowTitles_YouTube_ReturnsYouTube()
    {
        var titles = new[] { "Some App", "Rick Astley - Never Gonna Give You Up - YouTube" };
        Assert.Equal(MediaPlatform.YouTube, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_YouTubeOnly_ReturnsUnknown()
    {
        var titles = new[] { "youtube" };
        Assert.Equal(MediaPlatform.Unknown, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_SoundCloud_ReturnsSoundCloud()
    {
        var titles = new[] { "Artist - Track | SoundCloud" };
        Assert.Equal(MediaPlatform.SoundCloud, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_YouTubePrioritizedOverSoundCloud()
    {
        var titles = new[] { "Track - SoundCloud", "Video - YouTube" };
        Assert.Equal(MediaPlatform.YouTube, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Empty_ReturnsUnknown()
    {
        Assert.Equal(MediaPlatform.Unknown, PlatformDetector.DetectFromWindowTitles(Array.Empty<string>()));
    }

    [Fact]
    public void DetectFromWindowTitles_AppleMusic_ReturnsAppleMusic()
    {
        var titles = new[] { "Song - Apple Music" };
        Assert.Equal(MediaPlatform.AppleMusic, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Twitch_ReturnsTwitch()
    {
        var titles = new[] { "StreamerName - Playing Some Game - Twitch - Google Chrome" };
        Assert.Equal(MediaPlatform.Twitch, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Discord_ReturnsDiscord()
    {
        var titles = new[] { "#general | Cool Server - Discord" };
        Assert.Equal(MediaPlatform.Discord, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Vesktop_ReturnsDiscord()
    {
        var titles = new[] { "#voice-chat - Vesktop" };
        Assert.Equal(MediaPlatform.Discord, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Tidal_ReturnsTidal()
    {
        var titles = new[] { "Artist - Track - TIDAL" };
        Assert.Equal(MediaPlatform.Tidal, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Deezer_ReturnsDeezer()
    {
        var titles = new[] { "Artist - Track - Deezer" };
        Assert.Equal(MediaPlatform.Deezer, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Bandcamp_ReturnsBandcamp()
    {
        var titles = new[] { "Album Title | Bandcamp" };
        Assert.Equal(MediaPlatform.Bandcamp, PlatformDetector.DetectFromWindowTitles(titles));
    }

    [Fact]
    public void DetectFromWindowTitles_Netflix_ReturnsNetflix()
    {
        var titles = new[] { "Stranger Things - Netflix" };
        Assert.Equal(MediaPlatform.Netflix, PlatformDetector.DetectFromWindowTitles(titles));
    }

    #endregion

    #region IsBrowserApp

    [Theory]
    [InlineData("chrome.exe", true)]
    [InlineData("msedge.exe", true)]
    [InlineData("firefox.exe", true)]
    [InlineData("Brave.exe", true)]
    [InlineData("zen.exe", true)]
    [InlineData("arc.exe", true)]
    [InlineData("thorium.exe", true)]
    [InlineData("waterfox.exe", true)]
    [InlineData("floorp.exe", true)]
    [InlineData("librewolf.exe", true)]
    [InlineData("coccoc.exe", true)]
    [InlineData("Spotify.exe", false)]
    [InlineData("Discord.exe", false)]
    [InlineData("", false)]
    public void IsBrowserApp_IdentifiesCorrectly(string appId, bool expected)
    {
        Assert.Equal(expected, PlatformDetector.IsBrowserApp(appId));
    }

    [Fact]
    public void IsBrowserApp_Null_ReturnsFalse()
    {
        Assert.False(PlatformDetector.IsBrowserApp(null));
    }

    #endregion

    #region ExtractTitleFromWindow

    [Fact]
    public void ExtractTitleFromWindow_YouTube_StripsYouTubeSuffix()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("Rick Astley - Never Gonna Give You Up - YouTube", "YouTube");
        Assert.Equal("Rick Astley - Never Gonna Give You Up", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_Twitch_StripsTwitchSuffix()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("shroud - VALORANT - Twitch", "Twitch");
        Assert.Equal("shroud - VALORANT", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_Discord_StripsDiscordSuffix()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("#general | My Server - Discord", "Discord");
        Assert.Equal("#general | My Server", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_NotificationCount_Stripped()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("(3) Some Video - YouTube", "YouTube");
        Assert.Equal("Some Video", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_SoundCloud_StripsSuffix()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("Artist - Track - SoundCloud", "SoundCloud");
        Assert.Equal("Artist - Track", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_EmptyTitle_ReturnsPlatformFallback()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("", "YouTube");
        Assert.Equal("YouTube", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_BrowserSuffix_Stripped()
    {
        var result = PlatformDetector.ExtractTitleFromWindow("My Video - Google Chrome", "Browser");
        Assert.Equal("My Video", result);
    }

    [Fact]
    public void ExtractTitleFromWindow_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortTitle = new string('V', 32);
        Assert.Equal(shortTitle, PlatformDetector.ExtractTitleFromWindow($"{shortTitle} - YouTube", "YouTube"));

        string longTitle = new string('V', 1000);
        Assert.Equal(longTitle, PlatformDetector.ExtractTitleFromWindow($"{longTitle} - YouTube", "YouTube"));
    }

    #endregion

    #region ParseSpotifyTitle

    [Fact]
    public void ParseSpotifyTitle_ArtistAndTrack_ParsesCorrectly()
    {
        var (artist, track) = PlatformDetector.ParseSpotifyTitle("The Weeknd - Blinding Lights");
        Assert.Equal("The Weeknd", artist);
        Assert.Equal("Blinding Lights", track);
    }

    [Fact]
    public void ParseSpotifyTitle_TrackOnly_DefaultsArtistToSpotify()
    {
        var (artist, track) = PlatformDetector.ParseSpotifyTitle("Blinding Lights");
        Assert.Equal("Spotify", artist);
        Assert.Equal("Blinding Lights", track);
    }

    [Fact]
    public void ParseSpotifyTitle_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortArtist = new string('A', 20);
        string shortTrack = new string('T', 20);
        Assert.Equal((shortArtist, shortTrack), PlatformDetector.ParseSpotifyTitle($"{shortArtist} - {shortTrack}"));

        string longArtist = new string('A', 600);
        string longTrack = new string('T', 600);
        Assert.Equal((longArtist, longTrack), PlatformDetector.ParseSpotifyTitle($"{longArtist} - {longTrack}"));
    }

    #endregion

    #region NormalizeForLooseMatch

    [Fact]
    public void NormalizeForLooseMatch_RemovesDiacritics()
    {
        Assert.Equal("cafe", PlatformDetector.NormalizeForLooseMatch("Café"));
    }

    [Fact]
    public void NormalizeForLooseMatch_LowercasesAndStripsSpecialChars()
    {
        Assert.Equal("hello world", PlatformDetector.NormalizeForLooseMatch("Hello, World!"));
    }

    [Fact]
    public void NormalizeForLooseMatch_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", PlatformDetector.NormalizeForLooseMatch(""));
        Assert.Equal("", PlatformDetector.NormalizeForLooseMatch("   "));
    }

    [Fact]
    public void NormalizeForLooseMatch_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortUpper = new string('K', 24);
        Assert.Equal(new string('k', 24), PlatformDetector.NormalizeForLooseMatch($"{shortUpper}!"));

        string longUpper = new string('K', 1000);
        Assert.Equal(new string('k', 1000), PlatformDetector.NormalizeForLooseMatch($"{longUpper}!"));
    }

    #endregion

    #region HasReliableWindowMatch

    [Fact]
    public void HasReliableWindowMatch_TrackInTitle_ReturnsTrue()
    {
        var titles = new[] { "My Song - YouTube" };
        Assert.True(PlatformDetector.HasReliableWindowMatch(titles, "My Song", "youtube"));
    }

    [Fact]
    public void HasReliableWindowMatch_TrackNotInTitle_ReturnsFalse()
    {
        var titles = new[] { "Other Video - YouTube" };
        Assert.False(PlatformDetector.HasReliableWindowMatch(titles, "My Song", "youtube"));
    }

    [Fact]
    public void HasReliableWindowMatch_EmptyTrack_ReturnsFalse()
    {
        var titles = new[] { "Video - YouTube" };
        Assert.False(PlatformDetector.HasReliableWindowMatch(titles, "", "youtube"));
    }

    [Fact]
    public void TurkishCulture_HasReliableWindowMatch_RefineFromMetadata_AndDetectPlatform_HandleUppercaseDottedIInputs()
    {
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        CultureInfo prevUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = tr;
            CultureInfo.CurrentUICulture = tr;

            Assert.True(PlatformDetector.HasReliableWindowMatch("My Song - YOUTUBE", MediaPlatform.YouTube));
            Assert.True(PlatformDetector.HasReliableWindowMatch("My Song - SPOTIFY", MediaPlatform.Spotify));
            Assert.True(PlatformDetector.HasReliableWindowMatch("My Song - BILIBILI", MediaPlatform.Bilibili));
            Assert.True(PlatformDetector.HasReliableWindowMatch("My Song - APPLE MUSIC", MediaPlatform.AppleMusic));
            Assert.True(PlatformDetector.HasReliableWindowMatch(new[] { "My Song - YOUTUBE" }, "My Song", "YOUTUBE"));
            Assert.True(PlatformDetector.HasReliableWindowMatch(new[] { "My Song - SPOTIFY" }, "My Song", "SPOTIFY"));
            Assert.True(PlatformDetector.HasReliableWindowMatch(new[] { "My Song - BILIBILI" }, "My Song", "BILIBILI"));

            Assert.Equal(MediaPlatform.Spotify, PlatformDetector.DetectFromAppId("SPOTIFY"));
            Assert.Equal(MediaPlatform.Twitch, PlatformDetector.DetectFromAppId("TWITCH"));
            Assert.Equal(MediaPlatform.Tidal, PlatformDetector.DetectFromAppId("TIDAL"));
            Assert.Equal(MediaPlatform.Discord, PlatformDetector.DetectFromAppId("DISCORD"));

            var appleMusicAppInfo = new MediaInfo();
            MediaSourceClassifier.ApplyFromAppId(appleMusicAppInfo, "APPLEMUSIC");
            Assert.Equal(MediaPlatform.AppleMusic, appleMusicAppInfo.Platform);
            Assert.Equal(MediaPlatform.YouTube, PlatformDetector.DetectFromWindowTitles(new[] { "Song - YOUTUBE" }));
            Assert.Equal(MediaPlatform.Bilibili, PlatformDetector.DetectFromWindowTitles(new[] { "Song - BILIBILI" }));
            Assert.Equal(MediaPlatform.AppleMusic, PlatformDetector.DetectFromWindowTitles(new[] { "Song - APPLE MUSIC" }));
            Assert.Equal(MediaPlatform.YouTube, MediaPlatformExtensions.ParsePlatform("YOUTUBE"));
            Assert.Equal(MediaPlatform.Spotify, MediaPlatformExtensions.ParsePlatform("SPOTIFY"));
            Assert.Equal(MediaPlatform.Bilibili, MediaPlatformExtensions.ParsePlatform("BILIBILI"));

            var ytInfo = new MediaInfo { MediaSource = "Browser" };
            MediaSourceClassifier.RefineFromMetadata(ytInfo, "SONG - YOUTUBE", "", "");
            Assert.Equal(MediaPlatform.YouTube, ytInfo.Platform);

            var biliInfo = new MediaInfo { MediaSource = "Browser" };
            MediaSourceClassifier.RefineFromMetadata(biliInfo, "VIDEO - BILIBILI", "", "");
            Assert.Equal(MediaPlatform.Bilibili, biliInfo.Platform);

            var musicInfo = new MediaInfo { MediaSource = "Browser" };
            MediaSourceClassifier.RefineFromMetadata(musicInfo, "SONG", "ARTIST", "APPLE MUSIC");
            Assert.Equal(MediaPlatform.AppleMusic, musicInfo.Platform);

            var spotifyInfo = new MediaInfo();
            MediaSourceClassifier.ApplyFromAppId(spotifyInfo, "SPOTIFY");
            Assert.Equal(MediaPlatform.Spotify, spotifyInfo.Platform);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }

    #endregion

    #region MediaPlatformExtensions

    [Theory]
    [InlineData(MediaPlatform.Spotify, "Spotify")]
    [InlineData(MediaPlatform.YouTube, "YouTube")]
    [InlineData(MediaPlatform.SoundCloud, "SoundCloud")]
    [InlineData(MediaPlatform.Twitch, "Twitch")]
    [InlineData(MediaPlatform.Discord, "Discord")]
    [InlineData(MediaPlatform.Tidal, "TIDAL")]
    [InlineData(MediaPlatform.Deezer, "Deezer")]
    [InlineData(MediaPlatform.Bandcamp, "Bandcamp")]
    [InlineData(MediaPlatform.Netflix, "Netflix")]
    [InlineData(MediaPlatform.Unknown, "")]
    public void ToDisplayString_ReturnsExpected(MediaPlatform platform, string expected)
    {
        Assert.Equal(expected, platform.ToDisplayString());
    }

    [Theory]
    [InlineData("spotify", MediaPlatform.Spotify)]
    [InlineData("YouTube", MediaPlatform.YouTube)]
    [InlineData("twitch", MediaPlatform.Twitch)]
    [InlineData("discord", MediaPlatform.Discord)]
    [InlineData("vesktop", MediaPlatform.Discord)]
    [InlineData("tidal", MediaPlatform.Tidal)]
    [InlineData("deezer", MediaPlatform.Deezer)]
    [InlineData("bandcamp", MediaPlatform.Bandcamp)]
    [InlineData("netflix", MediaPlatform.Netflix)]
    [InlineData("unknown", MediaPlatform.Unknown)]
    [InlineData("", MediaPlatform.Unknown)]
    [InlineData(null, MediaPlatform.Unknown)]
    public void ParsePlatform_ReturnsExpected(string? input, MediaPlatform expected)
    {
        Assert.Equal(expected, MediaPlatformExtensions.ParsePlatform(input));
    }

    [Theory]
    [InlineData("Song - by Artist - SoundCloud", "by Artist", "Song")]
    [InlineData("Song – by Artist - SoundCloud", "by Artist", "Song")]
    [InlineData("Song — By Artist - SoundCloud", "By Artist", "Song")]
    [InlineData("Stream Midnight City by M83 | Listen online for free on SoundCloud - Google Chrome", "M83", "Midnight City")]
    [InlineData("▶ Sub Focus - Solar System by Sub Focus | Free Listening on SoundCloud - Microsoft Edge", "Sub Focus", "Solar System")]
    [InlineData("Lo-Fi Beats on SoundCloud - Brave", "SoundCloud", "Lo-Fi Beats")]
    public void ExtractAndParseSoundCloudTitle_RealBrowserWindowFormats_ExtractsArtistAndTrack(
        string windowTitle,
        string expectedArtist,
        string expectedTrack)
    {
        string extracted = PlatformDetector.ExtractTitleFromWindow(windowTitle, MediaPlatform.SoundCloud);
        var (artist, track) = PlatformDetector.ParseSoundCloudTitle(extracted);
        Assert.Equal(expectedArtist, artist);
        Assert.Equal(expectedTrack, track);
    }

    [Fact]
    public void ParseSoundCloudTitle_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortArtist = new string('A', 20);
        string shortTrack = new string('T', 20);
        Assert.Equal((shortArtist, shortTrack), PlatformDetector.ParseSoundCloudTitle($"{shortTrack} by {shortArtist}"));

        string longArtist = new string('A', 600);
        string longTrack = new string('T', 600);
        Assert.Equal((longArtist, longTrack), PlatformDetector.ParseSoundCloudTitle($"{longTrack} by {longArtist}"));
    }

    #endregion
}
