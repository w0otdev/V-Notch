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

    [Fact]
    public void BuildTrackIdentity_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortTrack = new string('T', 20);
        string shortArtist = new string('A', 20);
        Assert.Equal($"{new string('t', 20)}|{new string('a', 20)}",
            MediaHeuristics.BuildTrackIdentity(shortTrack, shortArtist));

        string longTrack = new string('T', 1000);
        string longArtist = new string('A', 1000);
        Assert.Equal($"{new string('t', 1000)}|{new string('a', 1000)}",
            MediaHeuristics.BuildTrackIdentity(longTrack, longArtist));
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

    [Fact]
    public void BuildSourceOverrideKey_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortSession = "sess-" + new string('x', 32);
        string longSession = "sess-" + new string('x', 1000);

        Assert.Equal(shortSession, MediaHeuristics.BuildSourceOverrideKey(shortSession, "Chrome"));
        Assert.Equal(longSession, MediaHeuristics.BuildSourceOverrideKey(longSession, "Chrome"));

        string longAppId = new string('p', 1000) + ".exe";
        Assert.Equal(longAppId, MediaHeuristics.BuildSourceOverrideKey("", longAppId));
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

    [Fact]
    public void NormalizeTrackForComparison_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortPrefix = new string('S', 30);
        Assert.Equal($"{shortPrefix} - Remix",
            MediaHeuristics.NormalizeTrackForComparison($"{shortPrefix} (Remix)"));

        string longPrefix = new string('L', 1000);
        Assert.Equal($"{longPrefix} - Remix",
            MediaHeuristics.NormalizeTrackForComparison($"{longPrefix} (Remix)"));
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

    [Fact]
    public void ExtractCoreTrackName_ShortAndLongInputs_MatchExpectedOutput()
    {
        string shortCore = new string('C', 30);
        Assert.Equal(shortCore,
            MediaHeuristics.ExtractCoreTrackName($"{shortCore} (Remastered 2011)"));

        string longCore = new string('C', 1000);
        Assert.Equal(longCore,
            MediaHeuristics.ExtractCoreTrackName($"{longCore} (Remastered 2011)"));
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

    [Fact]
    public void KoreanHangul_ExpandingNfkdInputs_DoNotThrowAcrossWrapperHelpers()
    {
        string artist = "방탄소년단";
        string title = "봄날 (Spring Day)";
        Assert.NotEmpty(MediaHeuristics.BuildTrackIdentity(title, artist));
        Assert.NotEmpty(MediaHeuristics.BuildSourceOverrideKey(title, artist));
        Assert.NotEmpty(MediaHeuristics.NormalizeTrackForComparison(title));
        Assert.NotEmpty(MediaHeuristics.ExtractCoreTrackName(title));
        Assert.NotEmpty(VNotch.Services.PlatformDetector.NormalizeForLooseMatch(artist));
        Assert.NotEmpty(VNotch.Services.SpotifyTrackMatcher.NormalizeForMatch(title));
        Assert.NotEmpty(VNotch.Services.SettingsSearchMatcher.Normalize(artist));
    }

    [Fact]
    public void MediaSessionState_CapsSessionsAndOverridesAtMaxCapacity()
    {
        var state = new VNotch.Services.MediaSessionState();
        for (int i = 0; i < 300; i++)
        {
            state.GetOrCreate($"session-{i}");
            state.SetSourceOverride($"key-{i}", "YouTube");
        }
        Assert.True(state.SessionCount <= 256);
        Assert.True(state.SourceOverrideCount <= 256);
    }

    #endregion
}
