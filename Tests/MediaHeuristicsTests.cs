using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaHeuristicsTests
{
    #region BuildTrackIdentity

    [Theory]
    [InlineData("Song", "Artist", "song|artist")]
    [InlineData("  Song  ", "  Artist  ", "song|artist")]
    [InlineData("SONG", "ARTIST", "song|artist")]
    [InlineData("", "", "|")]
    [InlineData("Track Only", "", "track only|")]
    public void BuildTrackIdentity_NormalizesTrimAndCase(string track, string artist, string expected)
    {
        Assert.Equal(expected, MediaHeuristics.BuildTrackIdentity(track, artist));
    }

    #endregion

    #region BuildSourceOverrideKey

    [Fact]
    public void BuildSourceOverrideKey_SessionKeyWins_WhenPresent()
    {
        Assert.Equal("sess-123", MediaHeuristics.BuildSourceOverrideKey("sess-123", "Chrome"));
    }

    [Fact]
    public void BuildSourceOverrideKey_BrowserApp_NoSession_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MediaHeuristics.BuildSourceOverrideKey("", "Chrome"));
        Assert.Equal(string.Empty, MediaHeuristics.BuildSourceOverrideKey("   ", "msedge"));
    }

    [Fact]
    public void BuildSourceOverrideKey_NonBrowserApp_NoSession_ReturnsAppId()
    {
        Assert.Equal("Spotify.exe", MediaHeuristics.BuildSourceOverrideKey("", "Spotify.exe"));
    }

    #endregion

    #region IsIgnoredSourceApp

    [Theory]
    [InlineData("Discord.exe", false)]
    [InlineData("discord", false)]
    [InlineData("Vesktop.Discord", false)]
    [InlineData("Spotify.exe", false)]
    [InlineData("Chrome", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsIgnoredSourceApp_ReturnsFalseForMediaSources(string? appId, bool expected)
    {
        Assert.Equal(expected, MediaHeuristics.IsIgnoredSourceApp(appId!));
    }

    #endregion

    #region IsTrackCompatibleWithWindowTitle

    [Fact]
    public void IsTrackCompatibleWithWindowTitle_TitleContainsTrack_True()
    {
        Assert.True(MediaHeuristics.IsTrackCompatibleWithWindowTitle(
            "Bohemian Rhapsody", "Bohemian Rhapsody - YouTube"));
    }

    [Fact]
    public void IsTrackCompatibleWithWindowTitle_Unrelated_False()
    {
        Assert.False(MediaHeuristics.IsTrackCompatibleWithWindowTitle(
            "Bohemian Rhapsody", "Some Unrelated Video Title"));
    }

    [Theory]
    [InlineData("", "Bohemian Rhapsody - YouTube")]
    [InlineData("Bohemian Rhapsody", "")]
    [InlineData("   ", "   ")]
    public void IsTrackCompatibleWithWindowTitle_EmptyInputs_False(string track, string title)
    {
        Assert.False(MediaHeuristics.IsTrackCompatibleWithWindowTitle(track, title));
    }

    #endregion

    #region IsLikelySoundCloudPlaceholderArtworkUrl

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://i1.sndcdn.com/default_avatar-large.png")]
    [InlineData("https://a-v2.sndcdn.com/images/default_artwork.png")]
    [InlineData("https://example.com/default-soundcloud-art.jpg")]
    [InlineData("https://i1.sndcdn.com/avatars-000-large.jpg")]
    public void IsLikelySoundCloudPlaceholderArtworkUrl_Placeholders_True(string? url)
    {
        Assert.True(MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(url));
    }

    [Fact]
    public void IsLikelySoundCloudPlaceholderArtworkUrl_RealArtwork_False()
    {
        Assert.False(MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(
            "https://i1.sndcdn.com/artworks-abc123-0-t500x500.jpg"));
    }

    [Fact]
    public void IsLikelySoundCloudPlaceholderArtworkUrl_HandlesEscapedSeparators()
    {
        Assert.True(MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(
            "https:\\/\\/i1.sndcdn.com\\/avatars-xyz-large.jpg"));
    }

    #endregion

    #region NormalizeTrackForComparison

    [Theory]
    [InlineData("Song (Remix)", "Song - Remix")]
    [InlineData("Song [Live]", "Song - Live")]
    [InlineData("Plain Title", "Plain Title")]
    public void NormalizeTrackForComparison_RewritesBrackets(string input, string expected)
    {
        Assert.Equal(expected, MediaHeuristics.NormalizeTrackForComparison(input));
    }

    #endregion

    #region ExtractCoreTrackName

    [Theory]
    [InlineData("Song (Remastered 2011)", "Song")]
    [InlineData("Song [Live]", "Song")]
    [InlineData("Song", "Song")]
    [InlineData("(Intro) Song", "(Intro) Song")]
    public void ExtractCoreTrackName_StripsTrailingBracketedSegment(string input, string expected)
    {
        Assert.Equal(expected, MediaHeuristics.ExtractCoreTrackName(input));
    }

    #endregion

    #region SpotifyTitleContainsTrack

    [Fact]
    public void SpotifyTitleContainsTrack_DirectMatch_True()
    {
        Assert.True(MediaHeuristics.SpotifyTitleContainsTrack(
            "Queen - Bohemian Rhapsody", "Bohemian Rhapsody"));
    }

    [Fact]
    public void SpotifyTitleContainsTrack_CoreNameMatch_True()
    {
        Assert.True(MediaHeuristics.SpotifyTitleContainsTrack(
            "Bohemian Rhapsody", "Bohemian Rhapsody (Remastered 2011)"));
    }

    [Fact]
    public void SpotifyTitleContainsTrack_NoMatch_False()
    {
        Assert.False(MediaHeuristics.SpotifyTitleContainsTrack(
            "Completely Different Title", "Bohemian Rhapsody"));
    }

    [Theory]
    [InlineData("", "Track")]
    [InlineData("Title", "")]
    public void SpotifyTitleContainsTrack_EmptyInputs_False(string title, string track)
    {
        Assert.False(MediaHeuristics.SpotifyTitleContainsTrack(title, track));
    }

    #endregion
}
