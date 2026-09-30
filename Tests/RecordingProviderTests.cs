using System.IO;
using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class RecordingProviderTests
{
    [Fact]
    public void WindowsProcessQuery_ReadsLiveProcessWithoutTreatingItAsCapture()
    {
        string? command = FfmpegRecordingProbe.ReadCommandLine(Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.False(FfmpegRecordingProbe.IsScreenCaptureCommand(command));
    }

    [Fact]
    public void FileOwnership_RequiresLiveOpenHandle()
    {
        string path = Path.GetTempFileName();
        using var process = Process.GetCurrentProcess();
        try
        {
            Assert.False(RecordingFileOwnership.IsFileOwnedBy(path, process));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Assert.True(RecordingFileOwnership.IsFileOwnedBy(path, process));
            }
            Assert.False(RecordingFileOwnership.IsFileOwnedBy(path, process));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Providers_FailureDoesNotMaskOtherRecordingEvidence()
    {
        Assert.True(new ScreenRecordingProbe(() => throw new IOException(), () => true).IsRecording());
        Assert.False(new ScreenRecordingProbe(() => throw new IOException(), () => false).IsRecording());
        Assert.False(new ScreenRecordingProbe(() => false, () => false).IsRecording());
    }

    [Theory]
    [InlineData("ffmpeg -f gdigrab -framerate 30 -i desktop out.mp4", true)]
    [InlineData("ffmpeg -f gdigrab -i \"title=Some Window\" out.mp4", true)]
    [InlineData("ffmpeg -f gdigrab -i hwnd=1234 out.mp4", true)]
    [InlineData("ffmpeg -f dshow -i video=screen-capture-recorder out.mp4", true)]
    [InlineData("ffmpeg -f lavfi -i ddagrab=output_idx=0 out.mp4", true)]
    [InlineData("ffmpeg -filter_complex \"ddagrab=framerate=60,hwdownload,format=bgra\" out.mp4", true)]
    [InlineData("ffmpeg -f lavfi -i gfxcapture=monitor_idx=0 out.mp4", true)]
    [InlineData("ffmpeg -i input.mp4 -metadata title=ddagrab output.mp4", false)]
    [InlineData("ffmpeg -f dshow -i video=Webcam out.mp4", false)]
    [InlineData("ffmpeg -f gdigrab -i desktop -frames:v 1 screenshot.png", false)]
    [InlineData("ffmpeg -f dshow -list_devices true -i dummy", false)]
    [InlineData("ffmpeg -f lavfi -i testsrc out.mp4", false)]
    [InlineData("ffmpeg -i gdigrab.mp4 out.mp4", false)]
    [InlineData("ffmpeg -f gdigrab -i desktop -h", false)]
    [InlineData("", false)]
    public void Ffmpeg_DistinguishesDesktopCaptureFromConversionAndScreenshots(string command, bool expected)
        => Assert.Equal(expected, FfmpegRecordingProbe.IsScreenCaptureCommand(command));

    [Fact]
    public void Obs_TracksOverlappingOutputsAndSplitLogWrites()
    {
        var state = new ObsRecordingProbe.LogState();
        state.Feed("12:00:00.000: settings: Recording Start\n");
        Assert.False(state.Active);
        state.Feed("12:00:00.000: ==== Recording Sta");
        Assert.False(state.Active);
        state.Feed("rt ===============================================\n");
        Assert.True(state.Active);
        state.Feed("12:00:01.000: ==== Streaming Start ===============================================\n");
        state.Feed("12:00:02.000: ==== Recording Stop ===============================================\n");
        Assert.True(state.Active);
        state.Feed("12:00:03.000: ==== Streaming Stop ===============================================\n");
        Assert.False(state.Active);
        state.Feed("12:00:04.000: ==== Replay Buffer Start ===============================================\n");
        Assert.True(state.Active);
        state.Feed("12:00:05.000: ==== Replay Buffer Stop ===============================================\n");
        Assert.False(state.Active);
    }

    [Fact]
    public void Obs_FileTailHandlesAppendAndTruncation()
    {
        string path = Path.GetTempFileName();
        try
        {
            var state = new ObsRecordingProbe.LogState();
            File.WriteAllText(path, "12:00:00.000: ==== Recording Start ===============================================\n");
            state.Read(path);
            Assert.True(state.Active);
            state.Read(path);
            Assert.True(state.Active);
            File.AppendAllText(path, "12:00:01.000: ==== Recording Stop ===============================================\n");
            state.Read(path);
            Assert.False(state.Active);
            File.AppendAllText(path, "12:00:02.000: ==== Recording Start ===============================================\n");
            state.Read(path);
            Assert.True(state.Active);
            File.WriteAllText(path, "new session\n");
            state.Read(path);
            Assert.False(state.Active);
        }
        finally { File.Delete(path); }
    }
}
