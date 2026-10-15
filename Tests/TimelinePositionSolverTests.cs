using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class TimelinePositionSolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TimelineSolveInputs Baseline => new()
    {
        StartTime = TimeSpan.Zero,
        EndTime = TimeSpan.FromMinutes(3),
        MaxSeekTime = TimeSpan.FromMinutes(3),
        Position = TimeSpan.FromSeconds(30),
        LastUpdatedUtc = Now,
        IsInitialOrBigChange = false,
        IsNewTrack = false,
        IsBrowserTimelineTrack = false,
        IsPlaying = false,
        PlaybackRate = 1.0,
        NowUtc = Now,
    };

    [Fact]
    public void Duration_FromEndMinusStart()
    {
        var r = TimelinePositionSolver.Solve(Baseline with { StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(190) });
        Assert.Equal(TimeSpan.FromSeconds(180), r.Duration);
    }

    [Fact]
    public void Duration_FallsBackToMaxSeekWhenEndNotAfterStart()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromSeconds(240),
            Position = TimeSpan.FromSeconds(10),
        });
        Assert.Equal(TimeSpan.FromSeconds(240), r.Duration);
    }

    [Fact]
    public void NotPlaying_PassesPositionThroughUnchanged()
    {
        var r = TimelinePositionSolver.Solve(Baseline);
        Assert.Equal(TimeSpan.FromSeconds(30), r.Position);
        Assert.Equal(Now, r.LastUpdatedUtc);
    }

    [Fact]
    public void NewTrackNearEnd_SnapsToStart()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsNewTrack = true,
            Position = TimeSpan.FromMinutes(3) - TimeSpan.FromMilliseconds(500),
        });

        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.False(r.IsIndeterminate);
        Assert.Equal(Now, r.LastUpdatedUtc);
    }

    [Fact]
    public void BrowserNewTrack_SuspiciousCarryOverWithStaleTimeline_ResetsToStart()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsBrowserTimelineTrack = true,
            IsNewTrack = true,
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(90),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(2),
        });

        Assert.Equal(TimeSpan.Zero, r.Position);
        Assert.Equal(Now, r.LastUpdatedUtc);
    }

    [Fact]
    public void BrowserNewTrack_FreshTimeline_KeepsPosition()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsBrowserTimelineTrack = true,
            IsNewTrack = true,
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(90),
            LastUpdatedUtc = Now - TimeSpan.FromMilliseconds(300),
        });

        Assert.True(r.Position >= TimeSpan.FromSeconds(90));
        Assert.True(r.Position < TimeSpan.FromSeconds(91));
    }

    [Fact]
    public void Playing_CompensatesForTimelineLatency()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(2),
        });

        Assert.Equal(32.0, r.Position.TotalSeconds, 2);
        Assert.Equal(Now, r.LastUpdatedUtc);
    }

    [Fact]
    public void Playing_CompensationRespectsPlaybackRate()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            PlaybackRate = 2.0,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(2),
        });

        Assert.Equal(34.0, r.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Playing_CompensationClampedToDuration()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            IsInitialOrBigChange = true,
            Position = TimeSpan.FromSeconds(150),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(120),
        });

        Assert.Equal(180.0, r.Position.TotalSeconds, 2);
    }

    [Fact]
    public void Playing_SmallLatencyBelowGate_NoCompensation()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromMilliseconds(50),
        });

        Assert.Equal(TimeSpan.FromSeconds(30), r.Position);
    }

    [Fact]
    public void Playing_StaleBeyondWindow_NoCompensation()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(30),
            LastUpdatedUtc = Now - TimeSpan.FromSeconds(20),
        });

        Assert.Equal(TimeSpan.FromSeconds(30), r.Position);
        Assert.Equal(Now - TimeSpan.FromSeconds(20), r.LastUpdatedUtc);
    }

    [Fact]
    public void PositionClampedToDuration()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            Position = TimeSpan.FromMinutes(5),
        });

        Assert.Equal(TimeSpan.FromMinutes(3), r.Position);
    }

    [Fact]
    public void UnknownDuration_MarksIndeterminate()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            EndTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.Zero,
            Position = TimeSpan.Zero,
        });

        Assert.Equal((bool?)true, r.IsIndeterminate);
    }

    [Fact]
    public void AbsurdDuration_MarksIndeterminate()
    {
        var r = TimelinePositionSolver.Solve(Baseline with
        {
            EndTime = TimeSpan.FromDays(40),
            MaxSeekTime = TimeSpan.FromDays(40),
        });

        Assert.Equal((bool?)true, r.IsIndeterminate);
        Assert.Equal(TimeSpan.Zero, r.Duration);
    }

    [Fact]
    public void NormalDuration_ExplicitlyClearsIndeterminate()
    {
        var r = TimelinePositionSolver.Solve(Baseline);
        Assert.False(r.IsIndeterminate);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void BrowserAtLiveEdge_NeverRewindsForNewTrackOrLatency(bool isNewTrack, double secondsBehindLive)
    {
        var result = TimelinePositionSolver.Solve(Baseline with
        {
            IsBrowserTimelineTrack = true,
            IsNewTrack = isNewTrack,
            IsPlaying = true,
            EndTime = TimeSpan.FromHours(2),
            MaxSeekTime = TimeSpan.FromHours(2),
            Position = TimeSpan.FromHours(2) - TimeSpan.FromSeconds(secondsBehindLive),
            LastUpdatedUtc = Now.AddSeconds(-2)
        });

        Assert.Equal(TimeSpan.FromHours(2), result.Position);
    }
}
