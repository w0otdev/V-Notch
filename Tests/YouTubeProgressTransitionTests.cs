using System.Windows;
using System.Windows.Input;
using VNotch.Controls;
using VNotch.Models;
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
