using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaTimelineSimulatorTests
{
    private readonly MediaTimelineSimulator _sim = new();

    #region Initial State

    [Fact]
    public void InitialState_NotThrottled()
    {
        Assert.False(_sim.IsThrottled);
    }

    [Fact]
    public void InitialState_ZeroPosition()
    {
        Assert.Equal(TimeSpan.Zero, _sim.LastObservedPosition);
    }

    #endregion

    #region UpdateObservedPosition

    [Fact]
    public void UpdateObservedPosition_UpdatesLastObserved()
    {
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), _sim.LastObservedPosition);
    }

    [Fact]
    public void UpdateObservedPosition_UpdatesChangeTime()
    {
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(10));
        Assert.True(_sim.LastPositionChangeTime > DateTime.MinValue);
    }

    [Fact]
    public async Task UpdateObservedPosition_SameValue_DoesNotUpdateTime()
    {
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(5));
        var firstTime = _sim.LastPositionChangeTime;

        await Task.Delay(10);
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(5));

        Assert.Equal(firstTime, _sim.LastPositionChangeTime);
    }

    #endregion

    #region IsPositionStuck

    [Fact]
    public void IsPositionStuck_InitialState_True()
    {
        Assert.True(_sim.IsPositionStuck(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IsPositionStuck_RecentUpdate_False()
    {
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(10));
        Assert.False(_sim.IsPositionStuck(TimeSpan.FromSeconds(5)));
    }

    #endregion

    #region EnterThrottledMode / Reset

    [Fact]
    public void EnterThrottledMode_SetsThrottled()
    {
        _sim.EnterThrottledMode();
        Assert.True(_sim.IsThrottled);
    }

    [Fact]
    public void Reset_ClearsThrottle()
    {
        _sim.EnterThrottledMode();
        _sim.Reset();
        Assert.False(_sim.IsThrottled);
    }

    [Fact]
    public void Reset_ClearsRecoveredData()
    {
        _sim.RecoveredDuration = TimeSpan.FromMinutes(3);
        _sim.Reset();
        Assert.Equal(TimeSpan.Zero, _sim.RecoveredDuration);
        Assert.Null(_sim.RecoveredThumbnail);
    }

    #endregion

    #region ResetRecoveredData

    [Fact]
    public void ResetRecoveredData_KeepsThrottleState()
    {
        _sim.EnterThrottledMode();
        _sim.RecoveredDuration = TimeSpan.FromMinutes(5);
        _sim.ResetRecoveredData();

        Assert.True(_sim.IsThrottled);
        Assert.Equal(TimeSpan.Zero, _sim.RecoveredDuration);
    }

    #endregion

    #region ApplySimulatedTimeline

    [Fact]
    public void ApplySimulatedTimeline_SetsThrottled()
    {
        var info = CreateMediaInfo(position: TimeSpan.FromSeconds(30), duration: TimeSpan.FromMinutes(3));
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(30));

        _sim.ApplySimulatedTimeline(info, atEndStuck: false);

        Assert.True(_sim.IsThrottled);
        Assert.True(info.IsThrottled);
    }

    [Fact]
    public void ApplySimulatedTimeline_AdvancesPosition()
    {
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(30));

        var info = CreateMediaInfo(position: TimeSpan.FromSeconds(30), duration: TimeSpan.FromMinutes(5));
        _sim.ApplySimulatedTimeline(info, atEndStuck: false);

        Assert.True(info.Position >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ApplySimulatedTimeline_ClampsToEndOfTrack()
    {
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(179));

        var info = CreateMediaInfo(position: TimeSpan.FromSeconds(179), duration: TimeSpan.FromMinutes(3));

        _sim.ApplySimulatedTimeline(info, atEndStuck: false);

        Assert.True(info.Position <= TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void ApplySimulatedTimeline_AtEndStuck_UsesRecoveredDuration()
    {
        _sim.RecoveredDuration = TimeSpan.FromMinutes(4);
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(100));

        var info = CreateMediaInfo(position: TimeSpan.FromSeconds(100), duration: TimeSpan.FromMinutes(3));
        _sim.ApplySimulatedTimeline(info, atEndStuck: true);

        Assert.Equal(TimeSpan.FromMinutes(4), info.Duration);
    }

    [Fact]
    public void ApplySimulatedTimeline_AppliesRecoveredThumbnail()
    {
        var thumb = new BitmapImage();
        _sim.RecoveredThumbnail = thumb;
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(10));

        var info = CreateMediaInfo(position: TimeSpan.FromSeconds(10), duration: TimeSpan.FromMinutes(3));
        info.Thumbnail = null;

        _sim.ApplySimulatedTimeline(info, atEndStuck: false);

        Assert.Same(thumb, info.Thumbnail);
    }

    #endregion

    #region TryExitThrottleIfPositionResumed

    [Fact]
    public void TryExitThrottleIfPositionResumed_RecentChange_ExitsThrottle()
    {
        _sim.EnterThrottledMode();
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(50));

        bool exited = _sim.TryExitThrottleIfPositionResumed(TimeSpan.FromSeconds(1));

        Assert.True(exited);
        Assert.False(_sim.IsThrottled);
    }

    [Fact]
    public void TryExitThrottleIfPositionResumed_NotThrottled_ReturnsFalse()
    {
        Assert.False(_sim.TryExitThrottleIfPositionResumed(TimeSpan.FromSeconds(1)));
    }

    #endregion

    #region TryExitThrottleIfStalled

    [Fact]
    public void TryExitThrottleIfStalled_LongStall_ExitsThrottle()
    {
        _sim.EnterThrottledMode();

        bool exited = _sim.TryExitThrottleIfStalled(TimeSpan.FromSeconds(3));

        Assert.True(exited);
        Assert.False(_sim.IsThrottled);
    }

    [Fact]
    public void TryExitThrottleIfStalled_RecentChange_DoesNotExit()
    {
        _sim.EnterThrottledMode();
        _sim.UpdateObservedPosition(TimeSpan.FromSeconds(10));

        bool exited = _sim.TryExitThrottleIfStalled(TimeSpan.FromSeconds(30));

        Assert.False(exited);
        Assert.True(_sim.IsThrottled);
    }

    #endregion

    [Theory]
    [InlineData(0, false)]
    [InlineData(7200, true)]
    [InlineData(3456000, false)]
    public void ApplySimulatedTimeline_LeavesLiveTimelineUntouched(double durationSeconds, bool indeterminate)
    {
        _sim.RecoveredDuration = TimeSpan.FromMinutes(3);
        _sim.UpdateObservedPosition(TimeSpan.FromMinutes(1));
        var info = CreateMediaInfo(TimeSpan.Zero, TimeSpan.FromSeconds(durationSeconds));
        info.IsIndeterminate = indeterminate;
        _sim.ApplySimulatedTimeline(info, atEndStuck: true);
        Assert.Equal(TimeSpan.Zero, info.Position);
        Assert.Equal(TimeSpan.FromSeconds(durationSeconds), info.Duration);
        Assert.False(info.IsThrottled);
        Assert.Equal(TimeSpan.Zero, _sim.RecoveredDuration);
    }

    private static MediaInfo CreateMediaInfo(TimeSpan position, TimeSpan duration)
    {
        return new MediaInfo
        {
            CurrentTrack = "Test Track",
            CurrentArtist = "Test Artist",
            IsPlaying = true,
            IsAnyMediaPlaying = true,
            Position = position,
            Duration = duration,
            PlaybackRate = 1.0
        };
    }
}
