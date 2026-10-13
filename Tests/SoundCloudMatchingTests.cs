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
}
