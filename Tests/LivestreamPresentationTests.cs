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
public sealed class LivestreamPresentationTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(7200, true)]
    [InlineData(7200, false)]
    [InlineData(3456000, true)]
    [InlineData(3456000, false)]
    public void LiveImmediatelyShowsLabelAndHasNoPerFrameCallback(double durationSeconds, bool playing) => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            host.Show();
            view.UpdateProgressTracking(LiveInfo(durationSeconds, playing));
            view.RenderProgressBar();

            Assert.Equal("LIVE", view.RemainingTimeText.Text);
            Assert.Equal("", view.CurrentTimeText.Text);
            Assert.Equal(Visibility.Visible, view.IndeterminateProgress.Visibility);
            Assert.Equal(Visibility.Collapsed, view.ProgressBar.Visibility);
            Assert.Equal(Cursors.Arrow, view.ProgressBarContainer.Cursor);
            Assert.Equal(playing, view.IndeterminateProgress.HasAnimatedProperties);
            Assert.False(Field<bool>(view, "_progressRenderingActive"));
        }
        finally { host.Close(); }
    });

    [Fact]
    public void VodToLiveCancelsRewindAndPauseKeepsLiveLabel() => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            host.Show();
            view.UpdateProgressTracking(new MediaInfo
            {
                CurrentTrack = "Previous video",
                SourceAppId = "chrome",
                MediaSource = "YouTube",
                IsPlaying = true,
                IsAnyMediaPlaying = true,
                IsSeekEnabled = true,
                Position = TimeSpan.FromSeconds(60),
                Duration = TimeSpan.FromMinutes(3)
            });
            view.UpdateProgressTracking(LiveInfo(0, true));
            Assert.False(Field<bool>(view, "_isRewindAnimating"));
            Assert.Equal("LIVE", view.RemainingTimeText.Text);
            view.UpdateProgressTracking(LiveInfo(0, false));
            Assert.Equal("LIVE", view.RemainingTimeText.Text);
            Assert.False(view.IndeterminateProgress.HasAnimatedProperties);
            Assert.False(Field<bool>(view, "_progressRenderingActive"));
        }
        finally { host.Close(); }
    });

    [Fact]
    public void IndeterminateWithPositiveDurationRejectsMouseAndProgrammaticSeek() => SharedStaTestRunner.RunAsync(async () =>
    {
        var media = new FakeMediaDetectionService();
        var model = new MediaViewModel(media, _ => Task.CompletedTask);
        using var view = new MediaPlaybackView();
        view.Attach(model, media);
        view.UpdateProgressTracking(LiveInfo(7200, true));
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left);
        typeof(MediaPlaybackView).GetMethod("ProgressBar_MouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object[] { view, down });
        Assert.False(down.Handled);
        Assert.False(Field<bool>(view, "_isClickSeekPending"));
        await view.SeekToPosition(TimeSpan.FromMinutes(1));
        Assert.Null(media.LastSeek);
        Assert.Equal(TimeSpan.Zero, view.GetPositionForRatio(.5));
    });

    [Fact]
    public void DvrDurationGrowth_KeepsTimelineIdentityAndRejectsStalePosition() => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        var first = LiveInfo(7200, true);
        first.Position = TimeSpan.FromMinutes(90);
        view.UpdateProgressTracking(first);
        string timelineKey = Field<string>(view, "_lastProgressTimelineKey");
        var next = first.Clone();
        next.Duration += TimeSpan.FromSeconds(1);
        next.LastUpdated = first.LastUpdated.AddSeconds(-5);
        next.Position = TimeSpan.FromMinutes(30);
        view.UpdateProgressTracking(next);
        Assert.Equal(timelineKey, Field<string>(view, "_lastProgressTimelineKey"));
        Assert.Equal(first.LastUpdated, Field<DateTimeOffset>(view, "_lastProgressTimelineUpdated"));
        Assert.Equal(first.Position, Field<ProgressEngine>(view, "_progressEngine").GetUiFrame().Position);
    });

    [Fact]
    public void LiveToPausedVod_SameTrack_RestoresFinitePresentation() => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            host.Show();
            view.UpdateProgressTracking(LiveInfo(0, true));
            var vod = LiveInfo(180, false);
            vod.IsIndeterminate = false;
            vod.Position = TimeSpan.FromSeconds(30);
            view.UpdateProgressTracking(vod);
            Assert.Equal("0:30", view.CurrentTimeText.Text);
            Assert.Equal("3:00", view.RemainingTimeText.Text);
            Assert.Equal(Visibility.Collapsed, view.IndeterminateProgress.Visibility);
            Assert.False(view.IndeterminateProgress.HasAnimatedProperties);
            Assert.Equal(Cursors.Hand, view.ProgressBarContainer.Cursor);
            Assert.False(Field<bool>(view, "_progressRenderingActive"));
        }
        finally { host.Close(); }
    });

    [Fact]
    public void OpeningDvrView_DoesNotStartCatchUpAnimationOrOverwriteLiveText() => SharedStaTestRunner.Run(() =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            var info = LiveInfo(7200, true);
            info.Position = TimeSpan.FromMinutes(90);
            view.UpdateProgressTracking(info);
            host.Show();
            view.StartProgressCatchUpAnimation();
            Assert.Equal("LIVE", view.RemainingTimeText.Text);
            Assert.False(Field<bool>(view, "_isCatchUpAnimating"));
            Assert.False(view.ProgressBarScale.HasAnimatedProperties);
            Assert.False(Field<bool>(view, "_progressRenderingActive"));
        }
        finally { host.Close(); }
    });

    private static MediaInfo LiveInfo(double durationSeconds, bool playing) => new()
    {
        CurrentTrack = "Live broadcast",
        CurrentArtist = "Channel",
        MediaSource = "YouTube",
        SourceAppId = "chrome",
        SessionInstanceKey = "tab",
        IsPlaying = playing,
        IsAnyMediaPlaying = true,
        IsIndeterminate = true,
        IsSeekEnabled = true,
        Duration = TimeSpan.FromSeconds(durationSeconds),
        Position = TimeSpan.Zero,
        LastUpdated = DateTimeOffset.UtcNow
    };

    private static T Field<T>(MediaPlaybackView view, string name) =>
        (T)typeof(MediaPlaybackView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
}
