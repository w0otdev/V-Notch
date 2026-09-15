using System;
using System.Collections.Generic;
using System.Linq;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LyricsServiceTests
{
    [Fact]
    public void ParseLrc_ExtractsSortedLinesAndFiltersHeaders()
    {
        const string lrc = """
            [ti:Test Title]
            [ar:Test Artist]
            [02:15.50]Third line
            [00:10.20]First line
            [01:05.80]Second line
            [03:00.00]
            [invalid]Malformed line
            """;

        var lines = LyricsService.ParseLrc(lrc);

        Assert.Equal(3, lines.Count);
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(10.20), lines[0].Time);

        Assert.Equal("Second line", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(65.80), lines[1].Time);

        Assert.Equal("Third line", lines[2].Text);
        Assert.Equal(TimeSpan.FromSeconds(135.50), lines[2].Time);
    }

    [Fact]
    public void ParseLrc_HandlesThreeDigitMilliseconds()
    {
        const string lrc = """
            [00:05.125]Precision line
            [00:10.5]Short fraction line
            """;

        var lines = LyricsService.ParseLrc(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(5125), lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(10500), lines[1].Time);
    }

    [Fact]
    public void ParseLrc_EmptyOrWhitespace_ReturnsEmptyList()
    {
        Assert.Empty(LyricsService.ParseLrc(""));
        Assert.Empty(LyricsService.ParseLrc("   \n\t  \n  "));
    }

    [Fact]
    public void GenerateSearchCandidates_DecomposesPipesAndCleansNoise()
    {
        var candidates = LyricsService.GenerateSearchCandidates(
            "Son Tung M-TP | Dung Lam Trai Tim Anh Dau (Official Music Video)",
            "YouTube");

        Assert.NotEmpty(candidates);

        // Verify generic "YouTube" was stripped from artist
        Assert.DoesNotContain(candidates, c => c.Artist.Equals("YouTube", StringComparison.OrdinalIgnoreCase));

        // Verify pipe decomposition generated candidate variations
        Assert.Contains(candidates, c => c.Track.Contains("Dung Lam Trai Tim Anh Dau", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateSearchCandidates_DecomposesDashesAndCleansCollaborators()
    {
        var candidates = LyricsService.GenerateSearchCandidates(
            "Alan Walker - Faded [Official Video]",
            "Alan Walker feat. Iselin Solheim");

        Assert.NotEmpty(candidates);

        // Verify noise bracket "[Official Video]" was stripped
        Assert.DoesNotContain(candidates, c => c.Track.Contains("[Official Video]"));

        // Verify candidate contains track "Faded"
        Assert.Contains(candidates, c => c.Track.Equals("Faded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseLrcMuxResult_ReturnsTimedLinesAndActualProvider()
    {
        const string json = """
            {
              "meta": {
                "level": "word",
                "source": { "id": "musixmatch", "name": "Musixmatch" }
              },
              "lines": [
                { "text": "Second line", "start": 2450, "end": 4000 },
                { "text": "First line", "start": 1200, "end": 2400 }
              ]
            }
            """;

        LyricsResult? result = LyricsService.ParseLrcMuxResult(json);

        Assert.NotNull(result);
        Assert.Equal("Musixmatch via lrc mux", result.Provider);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("First line", result.Lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), result.Lines[0].Time);
        Assert.Equal("Second line", result.Lines[1].Text);
    }

    [Fact]
    public void ParseLrcMuxResult_RejectsUnsynchronisedLyrics()
    {
        const string json = """
            {
              "meta": {
                "level": "none",
                "source": { "id": "genius", "name": "Genius" }
              },
              "lines": [
                { "text": "Plain lyric", "start": 0 }
              ]
            }
            """;

        Assert.Null(LyricsService.ParseLrcMuxResult(json));
    }

    [Theory]
    [InlineData("DƯỚI TÁN CÂY KHÔ HOA NỞ", "duoi tan cay kho hoa no")]
    [InlineData("JACK - J97 | DƯỚI TÁN CÂY KHÔ HOA NỞ (prod. Hino)", "jack j97 duoi tan cay kho hoa no")]
    [InlineData("Shape of You (Official Music Video)", "shape of you")]
    public void NormalizeForMatching_StripsPunctuationAndParentheses(string input, string expected)
    {
        string result = LyricsService.NormalizeForMatching(input);
        Assert.Equal(expected, result);
    }
}
