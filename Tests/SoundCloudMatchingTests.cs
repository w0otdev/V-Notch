using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SoundCloudMatchingTests
{
    [Theory]
    [InlineData("<a class='track' HREF = '/artist/test-song?in=playlist&amp;foo=bar'>Song</a>")]
    [InlineData("<a href=/artist/test-song>Song</a>")]
    [InlineData("<a href='https://soundcloud.com/artist/test-song'>Song</a>")]
    [InlineData("<a href='//www.soundcloud.com/artist/test-song'>Song</a>")]
    [InlineData("<a href='/artist/test&#45;song'>Song</a>")]
    public void SearchReadsHtmlLinksRegardlessOfAttributeFormatting(string html)
    {
        var candidate = Assert.Single(SoundCloudMatching.ExtractTrackUrlsFromSearchHtml(html, "Test song", "Artist"));
        Assert.Equal("https://soundcloud.com/artist/test-song", candidate.Url);
        Assert.True(candidate.Score > 0);
    }

    [Theory]
    [InlineData("<script>const example = 'https://soundcloud.com/artist/test-song';</script>")]
    [InlineData("<!-- <a href=\"/artist/test-song\"> -->")]
    [InlineData("<a href='https://example.org/?url=https://soundcloud.com/artist/test-song'>external</a>")]
    [InlineData("<a href='https://soundcloud.com.example.org/artist/test-song'>external</a>")]
    [InlineData("<a href='/artist/test-song/extra'>not a track</a>")]
    [InlineData("<a href='/artist/sets'>playlist</a><a href='/search/sounds'>navigation</a>")]
    public void SearchIgnoresTextCommentsAndNonTrackLinks(string html) =>
        Assert.Empty(SoundCloudMatching.ExtractTrackUrlsFromSearchHtml(html, "Test song", "Artist"));

    [Fact]
    public void SearchDeduplicatesAndRanksLinks()
    {
        const string html = "<a href='/other/unrelated-song'>Other</a>" +
            "<a href='/artist/test-song'>Song</a><a href='https://soundcloud.com/artist/test-song?x=1'>Duplicate</a>";
        var candidates = SoundCloudMatching.ExtractTrackUrlsFromSearchHtml(html, "Test song", "Artist");
        Assert.Equal(2, candidates.Count);
        Assert.Equal("https://soundcloud.com/artist/test-song", candidates[0].Url);
        Assert.True(candidates[0].Score > candidates[1].Score);
    }

    [Theory]
    [InlineData("https://i1.sndcdn.com/artworks-test-large.jpg")]
    [InlineData("https://i1.sndcdn.com/artworks-test-t200x200.jpg?signature=abc&size=original")]
    [InlineData("https://example.org/album-large.jpg")]
    public void ArtworkKeepsTheProviderUrlInsteadOfGuessingAResolution(string url) =>
        Assert.Equal(url, SoundCloudMatching.NormalizeArtworkUrl(url));

    [Theory]
    [InlineData("https://soundcloud.com/artist/track-slug", "https://soundcloud.com/artist/track-slug")]
    [InlineData("https://m.soundcloud.com/artist/track-slug?in=artist/sets/album", "https://soundcloud.com/artist/track-slug")]
    [InlineData("https://www.soundcloud.com/artist/track-slug/", "https://soundcloud.com/artist/track-slug")]
    public void ExtractTrackUrl_ValidTrackUrls_NormalizesToCanonicalUrl(string input, string expected) =>
        Assert.Equal(expected, SoundCloudMatching.ExtractTrackUrl(input));

    [Theory]
    [InlineData("https://soundcloud.com/you/likes")]
    [InlineData("https://soundcloud.com/search/sounds?q=test")]
    [InlineData("https://soundcloud.com/discover/sets")]
    [InlineData("https://soundcloud.com/artist/sets/my-playlist")]
    [InlineData("https://soundcloud.com/artist/popular-tracks")]
    [InlineData("https://soundcloud.com/artist/tracks")]
    public void ExtractTrackUrl_NonTrackOrMultiSegmentUrls_ReturnsNull(string input) =>
        Assert.Null(SoundCloudMatching.ExtractTrackUrl(input));

    [Fact]
    public void SanitizeSearchText_RemovesUnmatchedMiddleBracketsAndNoise()
    {
        string result = SoundCloudMatching.SanitizeSearchText("[Exclusive] Artist - SongName (Official Audio) [Prod. X]");
        Assert.Equal("Exclusive Artist - SongName", result);
    }

    [Fact]
    public void IsOEmbedMatch_HandlesSoundCloudTitleByAuthorFormatAndStrictArtistVerification()
    {
        Assert.True(SoundCloudMatching.IsOEmbedMatch(
            "Midnight City",
            "M83",
            "Midnight City by M83",
            "M83",
            candidateScore: 5,
            strictMode: true));

        Assert.False(SoundCloudMatching.IsOEmbedMatch(
            "Midnight City",
            "SoundCloud",
            "Unrelated Song by Other",
            "Other",
            candidateScore: 2,
            strictMode: true));
    }

    [Theory]
    [InlineData("NO POLE ULTIMATE 2", "NV So Lit")]
    [InlineData("NO POLE ULTIMATE (Sped Up)", "NV So Lit")]
    [InlineData("NO POLE", "NV So Lit")]
    [InlineData("NO POLE ULTIMATE", "Other uploader")]
    [InlineData("NO POLE ULTIMATE", "NV So Lit Archive")]
    [InlineData("NV So Lit - NO POLE ULTIMATE", "Other uploader")]
    public void ArtworkRejectsDifferentVersionsAndUploadersEvenWithAHighSearchScore(string title, string author)
    {
        foreach (bool strict in new[] { false, true })
            Assert.False(SoundCloudMatching.IsOEmbedMatch("NO POLE ULTIMATE", "NV So Lit", title, author, 8, strict));
    }

    [Theory]
    [InlineData("NO POLE ULTIMATE", "NO POLE ULTIMATE by NV So Lit")]
    [InlineData("NV So Lit - NO POLE ULTIMATE", "NO POLE ULTIMATE")]
    [InlineData("NO POLE ULTIMATE", "NV So Lit - NO POLE ULTIMATE")]
    [InlineData("NO POLE ULTIMATE (Official Audio)", "NO POLE ULTIMATE")]
    public void ArtworkStillMatchesEquivalentTitleFormatting(string expected, string candidate) =>
        Assert.True(SoundCloudMatching.IsOEmbedMatch(expected, "NV So Lit", candidate, "NV So Lit", 8, true));

    [Fact]
    public void ExactTrackSlugRanksAheadOfSequelsAndRemixes()
    {
        const string html = "<a href='/nv-so-lit/no-pole-ultimate-2'>Sequel</a>" +
            "<a href='/nv-so-lit/no-pole-ultimate-remix'>Remix</a>" +
            "<a href='/nv-so-lit/no-pole-ultimate-sped-up'>Sped up</a>" +
            "<a href='/nv-so-lit/no-pole-ultimate'>Original</a>";
        var candidates = SoundCloudMatching.ExtractTrackUrlsFromSearchHtml(html, "NO POLE ULTIMATE", "NV So Lit");
        Assert.Equal("https://soundcloud.com/nv-so-lit/no-pole-ultimate", candidates[0].Url);
        Assert.True(candidates[0].Score > candidates[1].Score);
    }
}
