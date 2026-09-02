using System;
using System.Collections.Generic;
using System.Linq;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class YouTubeSubtitleServiceTests
{
    [Fact]
    public void SelectCaptionTrack_NativePriority_PicksManualNativeOverAuto()
    {
        // Video with Japanese spoken audio (a.ja) and Japanese manual lyrics (.ja) + English (.en)
        var tracks = new List<YouTubeCaptionTrack>
        {
            new("en", "English", ".en", "https://url/en", false),
            new("ja", "Japanese", ".ja", "https://url/ja_manual", false),
            new("ja", "Japanese (auto-generated)", "a.ja", "https://url/ja_auto", true),
        };

        string? selected = YouTubeSubtitleService.SelectCaptionTrackUrl(tracks, ["native", "english", "auto"]);
        Assert.Equal("https://url/ja_manual", selected);
    }

    [Fact]
    public void SelectCaptionTrack_NativePriority_PicksAutoNativeIfNoManual()
    {
        // Video with Korean spoken audio (a.ko) and English manual (.en)
        var tracks = new List<YouTubeCaptionTrack>
        {
            new("en", "English", ".en", "https://url/en", false),
            new("ko", "Korean (auto-generated)", "a.ko", "https://url/ko_auto", true),
        };

        string? selected = YouTubeSubtitleService.SelectCaptionTrackUrl(tracks, ["native", "english", "auto"]);
        Assert.Equal("https://url/ko_auto", selected);
    }

    [Fact]
    public void SelectCaptionTrack_EnglishPriority_PicksEnglishFirst()
    {
        var tracks = new List<YouTubeCaptionTrack>
        {
            new("ja", "Japanese", ".ja", "https://url/ja_manual", false),
            new("en", "English", ".en", "https://url/en_manual", false),
            new("ja", "Japanese (auto)", "a.ja", "https://url/ja_auto", true),
        };

        string? selected = YouTubeSubtitleService.SelectCaptionTrackUrl(tracks, ["english", "native", "auto"]);
        Assert.Equal("https://url/en_manual", selected);
    }

    [Fact]
    public void SelectCaptionTrack_VietnameseVideo_PicksVietnameseManual()
    {
        var tracks = new List<YouTubeCaptionTrack>
        {
            new("vi", "Tiếng Việt", ".vi", "https://url/vi_manual", false),
            new("vi", "Tiếng Việt (tự động)", "a.vi", "https://url/vi_auto", true),
        };

        string? selected = YouTubeSubtitleService.SelectCaptionTrackUrl(tracks, ["native", "english", "auto"]);
        Assert.Equal("https://url/vi_manual", selected);
    }

    [Fact]
    public void ParseJson3_ExtractsAndSortsEventSegments()
    {
        const string json = """
        {
            "events": [
                {
                    "tStartMs": 5000,
                    "segs": [{ "utf8": "Hello " }, { "utf8": "World" }]
                },
                {
                    "tStartMs": 2000,
                    "segs": [{ "utf8": "First Line" }]
                }
            ]
        }
        """;

        var lines = YouTubeSubtitleService.ParseJson3(json);
        Assert.Equal(2, lines.Count);
        Assert.Equal("First Line", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), lines[0].Time);
        Assert.Equal("Hello World", lines[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), lines[1].Time);
    }

    [Fact]
    public void ParseXml_ExtractsAndSortsPAndTextElements()
    {
        const string xml = """
        <transcript>
            <text start="3.5" dur="1.2">Second subtitle</text>
            <text start="1.0" dur="2.0">First subtitle</text>
        </transcript>
        """;

        var lines = YouTubeSubtitleService.ParseXml(xml);
        Assert.Equal(2, lines.Count);
        Assert.Equal("First subtitle", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1.0), lines[0].Time);
        Assert.Equal("Second subtitle", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(3.5), lines[1].Time);
    }

    [Fact]
    public async Task LiveFetch_mA_UxOle3YQ_ReturnsSubtitles()
    {
        string rawYouTubeTitle = "JACK - J97 | XÓA TÊN ANH ĐI | Official Music Video | [Album26]";
        string rawYouTubeArtist = "YouTube";

        var lyricsService = new LyricsService();
        var lrc = await lyricsService.FetchSyncedLyricsAsync(rawYouTubeTitle, rawYouTubeArtist, 240);

        Assert.NotNull(lrc);
        Assert.NotEmpty(lrc.Lines);
    }
}
