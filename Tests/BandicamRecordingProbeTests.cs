using System.Runtime.InteropServices.ComTypes;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class BandicamRecordingProbeTests
{
    [Theory]
    [InlineData(@"C:\Videos\recording.mp4", true)]
    [InlineData(@"D:\Videos\recording.AVI", true)]
    [InlineData(@"C:\Videos\recording.wav", true)]
    [InlineData(@"C:\Videos\recording.mp3", true)]
    [InlineData(@"C:\Videos\recording.mp4.bfix", false)]
    [InlineData(@"C:\Videos\screenshot.png", false)]
    [InlineData(@"\\server\share\recording.mp4", false)]
    [InlineData(@"C:recording.mp4", false)]
    [InlineData("", false)]
    public void OutputPath_RejectsUnrelatedAndNetworkFiles(string path, bool expected)
        => Assert.Equal(expected, BandicamRecordingProbe.IsSupportedOutputPath(path));

    [Fact]
    public void Owner_MustMatchRecorderAndProcessStartTime()
    {
        long start = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var owner = new RecordingFileOwnership.UniqueProcess
        {
            ProcessId = 1234,
            StartTime = new FILETIME
            {
                dwHighDateTime = (int)(start >> 32),
                dwLowDateTime = unchecked((int)start)
            }
        };
        Assert.True(RecordingFileOwnership.MatchesRecorder(owner, 1234, start));
        Assert.False(RecordingFileOwnership.MatchesRecorder(owner, 5678, start));
        Assert.False(RecordingFileOwnership.MatchesRecorder(owner, 1234, start + 1));
    }
}
