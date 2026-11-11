using System;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed partial class FfmpegAndLyricsNativeTests
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ffmpeg_read_process_command_line(
        uint pid,
        Span<ushort> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_ffmpeg_is_screen_capture_command(
        ReadOnlySpan<ushort> commandPtr,
        int commandLen,
        out byte outIsCapture);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_bandicam_is_supported_output_path(
        ReadOnlySpan<ushort> pathPtr,
        int pathLen,
        out byte outSupported);

    [Fact]
    public void NativeFfmpegReadCommandLine_CurrentProcess_ReturnsNonEmptyCommandLine()
    {
        Span<char> buf = stackalloc char[2048];
        int status = vn_ffmpeg_read_process_command_line((uint)Environment.ProcessId, MemoryMarshal.Cast<char, ushort>(buf), buf.Length, out int len);
        if (status == -2 && len > buf.Length)
        {
            char[] heap = new char[len];
            status = vn_ffmpeg_read_process_command_line((uint)Environment.ProcessId, MemoryMarshal.Cast<char, ushort>(heap.AsSpan()), heap.Length, out len);
            Assert.Equal(0, status);
            Assert.False(string.IsNullOrWhiteSpace(new string(heap, 0, len)));
            return;
        }

        Assert.Equal(0, status);
        Assert.False(string.IsNullOrWhiteSpace(new string(buf[..len])));
    }

    [Theory]
    [InlineData("ffmpeg -f gdigrab -i desktop out.mp4", true)]
    [InlineData("ffmpeg -filter_complex ddagrab=0 -c:v libx264 out.mkv", true)]
    [InlineData("ffmpeg -i input.mp4 -c:v copy output.mp4", false)]
    [InlineData("", false)]
    public void NativeFfmpegScreenCaptureClassifier_MatchesExpectedResult(string command, bool expected)
    {
        int rc = vn_ffmpeg_is_screen_capture_command(MemoryMarshal.Cast<char, ushort>(command.AsSpan()), command.Length, out byte isCapture);
        Assert.Equal(0, rc);
        Assert.Equal(expected, isCapture != 0);
    }

    [Theory]
    [InlineData(@"C:\Captures\bandicam 2026.mp4", true)]
    [InlineData(@"C:\Captures\clip.avi", true)]
    [InlineData(@"C:\Captures\screenshot.png", false)]
    public void NativeBandicamOutputClassifier_MatchesExpectedResult(string path, bool expected)
    {
        int rc = vn_bandicam_is_supported_output_path(MemoryMarshal.Cast<char, ushort>(path.AsSpan()), path.Length, out byte supported);
        Assert.Equal(0, rc);
        Assert.Equal(expected, supported != 0);
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
