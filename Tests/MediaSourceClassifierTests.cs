using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaSourceClassifierTests
{
    #region ApplyFromAppId

    [Fact]
    public void ApplyFromAppId_EmptyAppId_LeavesSourceUnset()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "");
        Assert.Equal("", info.MediaSource);
    }

    [Fact]
    public void ApplyFromAppId_Spotify_SetsSourceAndFlags()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "Spotify.exe");

        Assert.Equal("Spotify", info.MediaSource);
        Assert.True(info.IsSpotifyPlaying);
        Assert.True(info.IsSpotifyRunning);
    }

    [Fact]
    public void ApplyFromAppId_YouTube_SetsSourceAndFlag()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "com.YouTube.desktop");

        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
    }

    [Fact]
    public void ApplyFromAppId_Discord_SetsSourceAndFlag()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "Discord.exe");

        Assert.Equal("Discord", info.MediaSource);
        Assert.True(info.IsDiscordRunning);
    }

    [Fact]
    public void ApplyFromAppId_Twitch_SetsSourceAndFlag()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "Twitch.exe");

        Assert.Equal("Twitch", info.MediaSource);
        Assert.True(info.IsTwitchRunning);
    }

    [Fact]
    public void ApplyFromAppId_Tidal_SetsSourceAndFlag()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "TIDAL.exe");

        Assert.Equal("TIDAL", info.MediaSource);
        Assert.True(info.IsTidalRunning);
    }

    [Theory]
    [InlineData("Chrome")]
    [InlineData("msedge")]
    [InlineData("Brave.Browser")]
    [InlineData("Zen")]
    [InlineData("Arc")]
    [InlineData("Thorium")]
    [InlineData("Floorp")]
    public void ApplyFromAppId_BrowserApp_ResolvesToBrowser(string appId)
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, appId);
        Assert.Equal("Browser", info.MediaSource);
    }

    [Theory]
    [InlineData("AppleMusic")]
    [InlineData("Apple.Music.Preview")]
    [InlineData("Microsoft.Music")]
    public void ApplyFromAppId_AppleMusic_SetsSourceAndFlag(string appId)
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, appId);

        Assert.Equal("Apple Music", info.MediaSource);
        Assert.True(info.IsAppleMusicRunning);
    }

    [Fact]
    public void ApplyFromAppId_UnknownNonBrowser_FallsBackToBrowser()
    {
        var info = new MediaInfo();
        MediaSourceClassifier.ApplyFromAppId(info, "com.unknown.player");
        Assert.Equal("Browser", info.MediaSource);
    }

    #endregion

    #region RefineFromMetadata

    [Fact]
    public void RefineFromMetadata_ResolvedNonBrowserSource_NoOp()
    {
        var info = new MediaInfo { MediaSource = "Spotify" };
        MediaSourceClassifier.RefineFromMetadata(info, "anything youtube", "", "");
        Assert.Equal("Spotify", info.MediaSource);
    }

    [Theory]
    [InlineData("watch this - youtube", "", "")]
    [InlineData("", "uploaded by youtube", "")]
    [InlineData("", "", "youtube")]
    public void RefineFromMetadata_BrowserWithYouTubeHint_ResolvesYouTube(string title, string artist, string album)
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, title, artist, album);

        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
    }

    [Fact]
    public void RefineFromMetadata_BrowserWithTwitchHint_ResolvesTwitch()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, "Live Stream - Twitch", "", "");

        Assert.Equal("Twitch", info.MediaSource);
        Assert.True(info.IsTwitchRunning);
    }

    [Fact]
    public void RefineFromMetadata_BrowserWithDiscordHint_ResolvesDiscord()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, "Voice Call", "Discord", "");

        Assert.Equal("Discord", info.MediaSource);
        Assert.True(info.IsDiscordRunning);
    }

    [Fact]
    public void RefineFromMetadata_EmptySourceWithAppleHint_ResolvesAppleMusic()
    {
        var info = new MediaInfo { MediaSource = "" };
        MediaSourceClassifier.RefineFromMetadata(info, "", "", "music.apple.com");

        Assert.Equal("Apple Music", info.MediaSource);
        Assert.True(info.IsAppleMusicRunning);
    }

    [Fact]
    public void RefineFromMetadata_BrowserWithSoundCloudHint_ResolvesSoundCloud()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, "", "soundcloud", "");

        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.True(info.IsSoundCloudRunning);
    }

    [Fact]
    public void RefineFromMetadata_BrowserWithNoHint_StaysBrowser()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.RefineFromMetadata(info, "regular song", "regular artist", "regular album");

        Assert.Equal("Browser", info.MediaSource);
        Assert.False(info.IsYouTubeRunning);
        Assert.False(info.IsSoundCloudRunning);
    }

    #endregion

    #region TryHandleJunkTitle

    [Theory]
    [InlineData("")]
    [InlineData("spotify")]
    [InlineData("Spotify Premium")]
    [InlineData("advertisement")]
    [InlineData("Windows Media Player")]
    [InlineData("chrome")]
    [InlineData("firefox")]
    [InlineData("discord")]
    [InlineData("twitch")]
    [InlineData("netflix")]
    public void TryHandleJunkTitle_JunkValues_ReturnsTrue(string title)
    {
        var info = new MediaInfo();
        Assert.True(MediaSourceClassifier.TryHandleJunkTitle(info, title, ""));
    }

    [Fact]
    public void TryHandleJunkTitle_YouTubeTitleWithRealArtist_NotJunk()
    {
        var info = new MediaInfo();
        Assert.False(MediaSourceClassifier.TryHandleJunkTitle(info, "Real Song Title", "Some Artist"));
    }

    [Fact]
    public void TryHandleJunkTitle_RealTitle_ReturnsFalse()
    {
        var info = new MediaInfo();
        Assert.False(MediaSourceClassifier.TryHandleJunkTitle(info, "Never Gonna Give You Up", "Rick Astley"));
    }

    [Fact]
    public void TryHandleJunkTitle_JunkOnYouTubeSource_ClearsTrackAndTagsArtist()
    {
        var info = new MediaInfo { MediaSource = "YouTube", CurrentTrack = "stale", CurrentArtist = "stale" };
        bool handled = MediaSourceClassifier.TryHandleJunkTitle(info, "advertisement", "");

        Assert.True(handled);
        Assert.Equal("", info.CurrentTrack);
        Assert.Equal("YouTube", info.CurrentArtist);
    }

    [Fact]
    public void TryHandleJunkTitle_JunkOnNonYouTubeSource_LeavesTrackUntouched()
    {
        var info = new MediaInfo { MediaSource = "Spotify", CurrentTrack = "keepme", CurrentArtist = "keepme" };
        bool handled = MediaSourceClassifier.TryHandleJunkTitle(info, "spotify", "");

        Assert.True(handled);
        Assert.Equal("keepme", info.CurrentTrack);
    }

    #endregion

    #region DetectFromWindowTitles

    [Fact]
    public void DetectFromWindowTitles_YouTubeTitle_ResolvesYouTube()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "My Video - YouTube" }, "", "", hasTrack: false);

        Assert.Equal("YouTube", info.MediaSource);
        Assert.True(info.IsYouTubeRunning);
    }

    [Fact]
    public void DetectFromWindowTitles_TwitchTitle_ResolvesTwitch()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "StreamerName - GameTitle - Twitch - Google Chrome" }, "", "", hasTrack: false);

        Assert.Equal("Twitch", info.MediaSource);
        Assert.True(info.IsTwitchRunning);
    }

    [Fact]
    public void DetectFromWindowTitles_DiscordTitle_ResolvesDiscord()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "#voice-channel | Gaming Server - Discord" }, "", "", hasTrack: false);

        Assert.Equal("Discord", info.MediaSource);
        Assert.True(info.IsDiscordRunning);
    }

    [Fact]
    public void DetectFromWindowTitles_SoundCloudTitle_ResolvesSoundCloud()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "Cool Track by Artist - SoundCloud" }, "", "", hasTrack: false);

        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.True(info.IsSoundCloudRunning);
    }

    [Theory]
    [InlineData("Some Album - Apple Music")]
    [InlineData("Listen on music.apple.com")]
    public void DetectFromWindowTitles_AppleMusicTitle_ResolvesAppleMusic(string title)
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { title }, "", "", hasTrack: false);

        Assert.Equal("Apple Music", info.MediaSource);
        Assert.True(info.IsAppleMusicRunning);
    }

    [Fact]
    public void DetectFromWindowTitles_FacebookWatch_ResolvesFacebook()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "Funny clip | Facebook Watch" }, "", "", hasTrack: false);

        Assert.Equal("Facebook", info.MediaSource);
        Assert.True(info.IsFacebookRunning);
    }

    [Fact]
    public void DetectFromWindowTitles_TikTok_ResolvesTikTok()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "creator | TikTok" }, "", "", hasTrack: false);

        Assert.Equal("TikTok", info.MediaSource);
        Assert.True(info.IsTikTokRunning);
    }

    [Fact]
    public void DetectFromWindowTitles_NoMatchingPlatform_LeavesSourceUnchanged()
    {
        var info = new MediaInfo { MediaSource = "Browser" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "Just a regular window title" }, "", "", hasTrack: false);

        Assert.Equal("Browser", info.MediaSource);
    }

    [Fact]
    public void DetectFromWindowTitles_AlreadyYouTube_IsNoOp()
    {
        var info = new MediaInfo { MediaSource = "YouTube" };
        MediaSourceClassifier.DetectFromWindowTitles(info, new[] { "Track - SoundCloud" }, "", "", hasTrack: false);

        Assert.Equal("YouTube", info.MediaSource);
    }

    [Fact]
    public void DetectFromWindowTitles_HasTrack_SkipsTitlesThatDoNotMatchTrack()
    {
        var info = new MediaInfo { MediaSource = "Browser", CurrentTrack = "My Song" };
        MediaSourceClassifier.DetectFromWindowTitles(
            info, new[] { "Unrelated tab - SoundCloud" }, trackTitleLower: "my song", trackTitleNormalized: "", hasTrack: true);

        Assert.Equal("Browser", info.MediaSource);
    }

    [Fact]
    public void DetectFromWindowTitles_HasTrack_MatchesTitleContainingTrack()
    {
        var info = new MediaInfo { MediaSource = "Browser", CurrentTrack = "My Song" };
        MediaSourceClassifier.DetectFromWindowTitles(
            info, new[] { "My Song - SoundCloud" }, trackTitleLower: "my song", trackTitleNormalized: "", hasTrack: true);

        Assert.Equal("SoundCloud", info.MediaSource);
        Assert.True(info.IsSoundCloudRunning);
    }

    #endregion
}
