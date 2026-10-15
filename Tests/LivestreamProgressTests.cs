using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class LivestreamProgressTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VodToLive_WithoutReset_DiscardsDurationAndAcceptsZeroPosition(bool isPlaying)
    {
        var engine = new ProgressEngine();
        engine.OnMediaSnapshot(Snapshot(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(60), 1));
        engine.GetUiFrame();

        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, TimeSpan.Zero, 2) with
        {
            IsIndeterminate = true,
            IsPlaying = isPlaying,
            IsSeekEnabled = false
        });

        var frame = engine.GetUiFrame();
        Assert.Equal(TimeSpan.Zero, frame.Duration);
        Assert.True(frame.ShowIndeterminate);
        Assert.True(frame.DurationJustChanged);
        Assert.InRange(frame.Position.TotalSeconds, 0, 1);
        Assert.Equal(isPlaying ? ProgressState.Playing : ProgressState.Paused, frame.State);
    }

    [Fact]
    public void LiveToVod_WithoutReset_ReportsDurationChange()
    {
        var engine = new ProgressEngine();
        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, TimeSpan.Zero, 1) with { IsIndeterminate = true });
        engine.GetUiFrame();

        engine.OnMediaSnapshot(Snapshot(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30), 2));

        var frame = engine.GetUiFrame();
        Assert.Equal(TimeSpan.FromMinutes(3), frame.Duration);
        Assert.False(frame.ShowIndeterminate);
        Assert.True(frame.DurationJustChanged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void TimelineModeChange_AcceptsUnchangedOrOlderSmtcTimestamp(double timestampOffset)
    {
        var engine = new ProgressEngine();
        DateTime timestamp = DateTime.UtcNow;
        engine.OnMediaSnapshot(Snapshot(TimeSpan.FromMinutes(3), TimeSpan.Zero, 1) with { Timestamp = timestamp });
        engine.GetUiFrame();

        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, TimeSpan.Zero, 2) with { Timestamp = timestamp.AddSeconds(timestampOffset) });

        var frame = engine.GetUiFrame();
        Assert.Equal(TimeSpan.Zero, frame.Duration);
        Assert.True(frame.ShowIndeterminate);
        Assert.True(frame.DurationJustChanged);
    }

    [Fact]
    public void VodToDvr_WithUnchangedPositiveDuration_RebasesAndReportsModeChange()
    {
        var engine = new ProgressEngine();
        var vod = Snapshot(TimeSpan.FromHours(2), TimeSpan.FromMinutes(60), 1);
        engine.OnMediaSnapshot(vod);
        engine.GetUiFrame();
        engine.OnMediaSnapshot(vod with { IsIndeterminate = true, SequenceNumber = 2 });

        var frame = engine.GetUiFrame();
        Assert.True(frame.ShowIndeterminate);
        Assert.True(frame.DurationJustChanged);
        Assert.False(engine.RequiresPositionUpdates);
    }

    [Fact]
    public void LiveWithStaleZeroPosition_DoesNotExtrapolateFromClock()
    {
        var engine = new ProgressEngine();
        engine.OnMediaSnapshot(Snapshot(TimeSpan.Zero, TimeSpan.Zero, 1) with { Timestamp = DateTime.UtcNow.AddSeconds(-10) });
        Assert.Equal(TimeSpan.Zero, engine.GetUiFrame().Position);
        Assert.False(engine.RequiresPositionUpdates);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(7200, true)]
    [InlineData(3456000, false)]
    public async Task ViewModel_VodToLive_DropsCachedDurationAndRejectsKeyboardSeek(double duration, bool indeterminate)
    {
        var media = new FakeMediaDetectionService();
        var model = new ProgressViewModel(media) { IsExpandedCheck = () => true };
        var info = new MediaInfo
        {
            CurrentTrack = "Broadcast",
            SourceAppId = "chrome",
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            IsSeekEnabled = true,
            Duration = TimeSpan.FromMinutes(3),
            Position = TimeSpan.FromMinutes(1)
        };
        model.Update(info);
        info = info.Clone();
        info.Duration = TimeSpan.FromSeconds(duration);
        info.Position = TimeSpan.Zero;
        info.IsIndeterminate = indeterminate;
        model.Update(info);
        model.Render();
        Assert.True(model.IsIndeterminate);
        Assert.False(model.HasTimeline);
        Assert.False(model.IsSeekEnabled);
        Assert.Equal("LIVE", model.RemainingTimeText);
        Assert.Equal("", model.CurrentTimeText);
        Assert.Equal(0, model.Position);
        Assert.Equal(TimeSpan.Zero, model.GetDragSeekPosition(.5));
        await model.SeekRelative(10);
        await model.SeekToPosition(TimeSpan.FromMinutes(1));
        Assert.Null(media.LastSeek);
    }

    private static ProgressSnapshot Snapshot(TimeSpan duration, TimeSpan position, long sequence) => new()
    {
        Duration = duration,
        Position = position,
        IsPlaying = true,
        PlaybackRate = 1,
        IsSeekEnabled = true,
        Timestamp = DateTime.UtcNow,
        SequenceNumber = sequence
    };
}
