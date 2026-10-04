using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ParseLrc_HandlesLineEndingsAndInvalidTimestampsWithoutLosingText(string newline)
    {
        string lrc = string.Join(newline, "[ti:Header]", "[00:02.125]  Đêm nay 🎵  ",
            "[not:a.time]Invalid", "[00:01.5] First ", "[00:03.00]   ");
        var lines = LyricsService.ParseLrc(lrc);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new LyricLine(TimeSpan.FromMilliseconds(1500), "First"), lines[0]);
        Assert.Equal(new LyricLine(TimeSpan.FromMilliseconds(2125), "Đêm nay 🎵"), lines[1]);
    }

    [Theory]
    [InlineData("ĐẶNG -- DƯỚI TÁN CÂY (live) [MV]")]
    [InlineData("Cafe\u0301 -- Straße / Æ / K / Å")]
    [InlineData("한글 日本語 Ελληνικά -- Song 123")]
    [InlineData("[intro) SONG (unfinished")]
    [InlineData("()[] 🎵 -- \u0301\u2003")]
    [InlineData("a\u0344b\u0903c")]
    public void NormalizeForMatching_PreservesOriginalUnicodeAndBracketSemantics(string input)
    {
        Assert.Equal(NormalizeOriginal(input), LyricsService.NormalizeForMatching(input));
    }

    [Fact]
    public void NormalizeForMatching_PooledBuffersRemainCorrectAcrossConcurrentCalls()
    {
        string input = string.Concat(Enumerable.Repeat("ĐÊM Cafe\u0301 🎵 -- SONG [MV] ", 80));
        string expected = NormalizeOriginal(input);
        Parallel.For(0, 40, _ => Assert.Equal(expected, LyricsService.NormalizeForMatching(input)));
    }

    [Fact]
    public void NormalizeForMatching_AllocatesLessThanOriginalPipeline()
    {
        const string input = "  ARTIST *** Song -- 2026 [Official Video]  ";
        long original = MeasureAllocations(() => NormalizeOriginal(input));
        long optimized = MeasureAllocations(() => LyricsService.NormalizeForMatching(input));
        Assert.True(optimized < original / 2, $"Original: {original} bytes; optimized: {optimized} bytes");
    }

    [Theory]
    [InlineData("en-US", "Song - LIVE")]
    [InlineData("tr-TR", "Song - LIVE")]
    [InlineData("az-Latn-AZ", "Song - LIVE")]
    [InlineData("lt-LT", "Song - LIVE")]
    [InlineData("en-US", "Song | OFFİCİAL Video")]
    [InlineData("tr-TR", "Song | OFFİCİAL Video")]
    [InlineData("az-Latn-AZ", "Song | OFFİCİAL Video")]
    [InlineData("lt-LT", "Song | OFFİCİAL Video")]
    public void GenerateSearchCandidates_PreservesCultureSensitiveSuffixMatching(string culture, string title)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string expected = Regex.Replace(title, @"\s*\|\s*(?:Official|OFFICIAL|MV|mv|Music Video|Visualizer|Lyric Video|Audio|Track\s*No\.\d+).*", "", RegexOptions.IgnoreCase);
            expected = Regex.Replace(expected, @"\s*-\s*(?:Remaster(?:ed)?|Live|Acoustic|Radio Edit|Bonus Track|Single Version|Instrumental|Deluxe|Mono|Stereo).*", "", RegexOptions.IgnoreCase).Trim();
            Assert.Equal(expected, LyricsService.GenerateSearchCandidates(title, "Artist")[0].Track);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static long MeasureAllocations(Func<string> normalize)
    {
        for (int i = 0; i < 100; i++) normalize();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) GC.KeepAlive(normalize());
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string NormalizeOriginal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string stripped = Regex.Replace(text, @"[\(\[][^\)\]]*[\)\]]", "");
        string decomposed = stripped.Normalize(NormalizationForm.FormD);
        var buffer = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                buffer.Append(c is 'đ' or 'Đ' ? 'd' : c);
        }
        return Regex.Replace(buffer.ToString().Normalize(NormalizationForm.FormC), @"[^a-zA-Z0-9]+", " ")
            .Trim().ToLowerInvariant();
    }
}
