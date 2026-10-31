using System;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class FfmpegAndLyricsNativeTests
{
    [Fact]
    public void FfmpegRecordingProbe_TransientNullCommandLine_IsNotCachedAsFalse()
    {
        int calls = 0;
        var probe = new FfmpegRecordingProbe(_ =>
        {
            calls++;
            return calls == 1 ? null : "ffmpeg -f gdigrab -i desktop out.mp4";
        });

        Assert.False(probe.CheckProcess(4242, 1000));
        Assert.True(probe.CheckProcess(4242, 1000));
        Assert.True(probe.CheckProcess(4242, 1000));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FfmpegRecordingProbe_ReadCommandLine_CurrentProcess_ReturnsNonEmptyCommandLine()
    {
        string? cmd = FfmpegRecordingProbe.ReadCommandLine(Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(cmd));
    }

    [Fact]
    public void LyricsService_GenerateSearchCandidates_DecomposesDashesAndCleansBothSides()
    {
        var candidates = LyricsService.GenerateSearchCandidates("Artist - Song Title (Official Video)", "");

        Assert.Contains(("Song Title", "Artist"), candidates);
        Assert.Contains(("Artist", "Song Title"), candidates);

        var liveCandidates = LyricsService.GenerateSearchCandidates("Artist - Live Forever (Official Video)", "");
        Assert.Contains(("Live Forever", "Artist"), liveCandidates);
        Assert.Contains(("Artist", "Live Forever"), liveCandidates);
    }

    [Theory]
    [InlineData("Đừng Làm Trái Tim Anh Đau (Official Video) [4K]", "dung lam trai tim anh dau")]
    [InlineData("Beyoncé - Halo!", "beyonce halo")]
    [InlineData("  Hello --- World 123  ", "hello world 123")]
    public void LyricsService_NormalizeForMatching_ProducesExpectedNormalizedText(string input, string expected)
    {
        string actual = LyricsService.NormalizeForMatching(input);

        Assert.Equal(expected, actual);
    }
}
