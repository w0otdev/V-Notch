using System.Reflection;
using System.Windows;
using System.Windows.Input;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class YouTubeProgressTransitionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrackSwitch_WithPendingTimeline_ShowsPlaceholderThenRestoresProgress(bool playing) => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        view.UpdateProgressTracking(Track("Previous video", 180, playing));
        view.RenderProgressBar();

        var pending = Track("Next video", 0, playing);
        pending.IsTimelinePending = true;
        view.UpdateProgressTracking(pending);
        view.RenderProgressBar();

        Assert.Equal("--:--", view.RemainingTimeText.Text);
        Assert.Equal("", view.CurrentTimeText.Text);
        Assert.Equal(Cursors.Arrow, view.ProgressBarContainer.Cursor);
        Assert.Equal(0, view.ProgressBarScale.ScaleX);

        var ready = pending.Clone();
        ready.IsTimelinePending = false;
        ready.Duration = TimeSpan.FromMinutes(4);
        ready.Position = TimeSpan.FromSeconds(1);
        view.UpdateProgressTracking(ready);
        view.RenderProgressBar();

        Assert.Equal("4:00", view.RemainingTimeText.Text);
        Assert.Equal("0:01", view.CurrentTimeText.Text);
        Assert.Equal(Visibility.Collapsed, view.IndeterminateProgress.Visibility);
        Assert.Equal(Cursors.Hand, view.ProgressBarContainer.Cursor);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ViewModel_PendingTrack_DoesNotShowLiveOrSeekBeforeDurationArrives(bool playing)
    {
        var media = new FakeMediaDetectionService();
        var model = new ProgressViewModel(media) { IsExpandedCheck = () => true };
        model.Update(Track("Previous video", 180, playing));

        var pending = Track("Next video", 0, playing);
        pending.IsTimelinePending = true;
        model.Update(pending);
        model.Render();

        Assert.Equal("--:--", model.RemainingTimeText);
        Assert.Equal("", model.CurrentTimeText);
        Assert.False(model.HasTimeline);
        Assert.False(model.IsSeekEnabled);
        await model.SeekRelative(10);
        Assert.Null(media.LastSeek);

        var ready = pending.Clone();
        ready.IsTimelinePending = false;
        ready.Duration = TimeSpan.FromMinutes(4);
        ready.Position = TimeSpan.FromSeconds(1);
        model.Update(ready);
        model.Render();

        Assert.Equal("4:00", model.RemainingTimeText);
        Assert.Equal("0:01", model.CurrentTimeText);
        Assert.True(model.HasTimeline);
        Assert.True(model.IsSeekEnabled);
    }

    [Fact]
    public void TimelineSimulator_PositionZeroAndAfterReset_IsNotImmediatelyStuck()
    {
        var simulator = new MediaTimelineSimulator();

        Assert.False(simulator.IsPositionStuck(TimeSpan.FromSeconds(1.5)));

        simulator.UpdateObservedPosition(TimeSpan.Zero);
        Assert.NotEqual(DateTime.MinValue, simulator.LastPositionChangeTime);
        Assert.False(simulator.IsPositionStuck(TimeSpan.FromSeconds(1.5)));

        simulator.UpdateObservedPosition(TimeSpan.FromSeconds(25));
        simulator.Reset();

        Assert.Equal(TimeSpan.Zero, simulator.LastObservedPosition);
        Assert.Equal(DateTime.MinValue, simulator.LastPositionChangeTime);
        Assert.False(simulator.IsPositionStuck(TimeSpan.FromSeconds(1.5)));

        simulator.UpdateObservedPosition(TimeSpan.Zero);
        Assert.NotEqual(DateTime.MinValue, simulator.LastPositionChangeTime);
        Assert.False(simulator.IsPositionStuck(TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void WindowTitleRecovery_SwitchingToNewTrack_PreservesThrottledStateAfterBookkeeping()
    {
        using var service = new MediaDetectionService(null!, null!, null!);
        SeedStuckTimelineState(service, lastTrackName: "Previous Video", stuckAt: TimeSpan.FromSeconds(10));

        var info = new MediaInfo
        {
            CurrentTrack = "",
            MediaSource = "YouTube",
            IsPlaying = true,
            IsAnyMediaPlaying = true,
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(100)
        };

        InvokeVideoTimelineRecovery(service, info, ["Next Video - YouTube"]);
        InvokeTrackNameChangeBookkeeping(service, info);
        info.IsThrottled = GetTimelineSimulator(service).IsThrottled;

        Assert.Equal("Next Video", info.CurrentTrack);
        Assert.True(GetTimelineSimulator(service).IsThrottled);
        Assert.True(info.IsThrottled);
    }

    [Fact]
    public void WindowTitleRecovery_SubstringTitleAndBlankWindowTitle_ResetsRecoveredDurationOnlyForValidNewTrack()
    {
        using var service = new MediaDetectionService(null!, null!, null!);
        SeedStuckTimelineState(service, lastTrackName: "Song (Live)", stuckAt: TimeSpan.FromSeconds(10));
        var simulator = GetTimelineSimulator(service);
        simulator.RecoveredDuration = TimeSpan.FromMinutes(5);

        var blankInfo = new MediaInfo
        {
            CurrentTrack = "",
            MediaSource = "YouTube",
            IsPlaying = true,
            IsAnyMediaPlaying = true,
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(100)
        };

        InvokeVideoTimelineRecovery(service, blankInfo, ["   - YouTube"]);

        Assert.Equal("", blankInfo.CurrentTrack);
        Assert.Equal(TimeSpan.FromMinutes(5), simulator.RecoveredDuration);

        var nextInfo = new MediaInfo
        {
            CurrentTrack = "",
            MediaSource = "YouTube",
            IsPlaying = true,
            IsAnyMediaPlaying = true,
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(100)
        };

        InvokeVideoTimelineRecovery(service, nextInfo, ["Song - YouTube"]);

        Assert.Equal("Song", nextInfo.CurrentTrack);
        Assert.Equal(TimeSpan.Zero, simulator.RecoveredDuration);
        Assert.Equal(TimeSpan.Zero, nextInfo.Duration);
        Assert.True(nextInfo.IsThrottled);
    }

    private static MediaTimelineSimulator GetTimelineSimulator(MediaDetectionService service) =>
        (MediaTimelineSimulator)typeof(MediaDetectionService)
            .GetField("_timelineSimulator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static void SeedStuckTimelineState(MediaDetectionService service, string lastTrackName, TimeSpan stuckAt)
    {
        var staleUtc = DateTime.UtcNow - TimeSpan.FromSeconds(5);
        var serviceType = typeof(MediaDetectionService);
        serviceType.GetField("_lastTrackName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, lastTrackName);
        serviceType.GetField("_lastMetadataChangeTime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, staleUtc);

        var simulator = GetTimelineSimulator(service);
        simulator.UpdateObservedPosition(stuckAt);
        typeof(MediaTimelineSimulator)
            .GetField("_lastPositionChangeTime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(simulator, staleUtc);
    }

    private static void InvokeVideoTimelineRecovery(MediaDetectionService service, MediaInfo info, List<string> windowTitles)
    {
        var method = typeof(MediaDetectionService).GetMethod(
            "ApplyVideoTimelineRecovery",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        object?[] args = [info, windowTitles];
        method.Invoke(service, args);
    }

    private static void InvokeTrackNameChangeBookkeeping(MediaDetectionService service, MediaInfo info)
    {
        var method = typeof(MediaDetectionService).GetMethod(
            "TrackNameChangeBookkeeping",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var parameters = method.GetParameters();
        object?[] args = parameters.Length == 1
            ? [info]
            : [info, parameters[1].Name?.Contains("reset", StringComparison.OrdinalIgnoreCase) != true];
        method.Invoke(service, args);
    }

    private static MediaInfo Track(string title, double durationSeconds, bool playing) => new()
    {
        CurrentTrack = title,
        CurrentArtist = "Channel",
        MediaSource = "YouTube",
        SourceAppId = "chrome",
        SessionInstanceKey = "tab",
        IsPlaying = playing,
        IsAnyMediaPlaying = true,
        IsSeekEnabled = true,
        Duration = TimeSpan.FromSeconds(durationSeconds),
        Position = TimeSpan.Zero,
        LastUpdated = DateTimeOffset.UtcNow
    };
}
