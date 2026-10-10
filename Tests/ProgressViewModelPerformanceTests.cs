using VNotch.Models;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class ProgressViewModelPerformanceTests
{
    [Theory]
    [InlineData(15.01, 15.99, 90, "0:15", "1:30")]
    [InlineData(59.99, 60, 3600, "1:00", "1:00:00")]
    [InlineData(3599.99, 3600, 7200, "1:00:00", "2:00:00")]
    [InlineData(3600.5, 15.5, 7200, "0:15", "2:00:00")]
    public void TimelineTextTracksSecondsAcrossUpdatesAndBackwardSeeks(
        double initialPosition, double nextPosition, double duration, string expectedPosition, string expectedDuration)
    {
        var model = new ProgressViewModel(new FakeMediaDetectionService());
        model.Update(Timeline(initialPosition, duration));
        model.Render();
        model.Update(Timeline(nextPosition, duration));
        model.Render();

        Assert.Equal(expectedPosition, model.CurrentTimeText);
        Assert.Equal(expectedDuration, model.RemainingTimeText);
        Assert.Equal(nextPosition / duration, model.Position, 10);
    }

    [Fact]
    public void TimelineTextRecoversAfterResetAndDurationChanges()
    {
        var model = new ProgressViewModel(new FakeMediaDetectionService());
        model.Update(Timeline(15.75, 90));
        model.Render();
        model.Update(new MediaInfo());
        model.Render();
        Assert.Equal("--:--", model.CurrentTimeText);
        model.Update(Timeline(15.75, 120));
        model.Render();
        Assert.Equal("0:15", model.CurrentTimeText);
        Assert.Equal("2:00", model.RemainingTimeText);

        model.StartDragging();
        model.UpdateDragPosition(0.5);
        Assert.Equal("1:00", model.CurrentTimeText);
        model.StopDragging();
        model.Render();
        Assert.Equal("0:15", model.CurrentTimeText);
    }

    [Fact]
    public void UnchangedTimelineFramesDoNotAllocateTimeStrings()
    {
        var model = new ProgressViewModel(new FakeMediaDetectionService());
        model.Update(Timeline(15.75, 90));
        for (int i = 0; i < 100; i++) model.Render();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) model.Render();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(0, allocated);
        Assert.Equal("0:15", model.CurrentTimeText);
        Assert.Equal("1:30", model.RemainingTimeText);
    }

    private static MediaInfo Timeline(double position, double duration) => new()
    {
        IsAnyMediaPlaying = true,
        IsPlaying = false,
        Duration = TimeSpan.FromSeconds(duration),
        Position = TimeSpan.FromSeconds(position),
        CurrentTrack = "Track",
        CurrentArtist = "Artist",
        SourceAppId = "player",
        MediaSource = "Spotify"
    };
}
