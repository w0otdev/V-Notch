using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowProgressPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 98.5)]
    [InlineData(120, 98.5)]
    public void UserSeekClampsToTrackAndUpdatesRenderedTime(double requested, double expected) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        using var fixture = CreateFixture(media);
        var window = fixture.Window;
        Prepare(window, 30, false, false);
        Invoke(window, "RenderProgressBar");
        Assert.Equal(.3, window.ProgressBarScale.ScaleX, 3);
        Assert.Equal("0:30", window.CurrentTimeText.Text);
        await (Task)Invoke(window, "SeekToPosition", TimeSpan.FromSeconds(requested))!;
        Assert.Equal(TimeSpan.FromSeconds(expected), media.LastSeek);
        Assert.Equal(MediaProgressHelpers.FormatTime(TimeSpan.FromSeconds(expected)), window.CurrentTimeText.Text);
        Assert.InRange(Field<double>(window, "_progressTargetRatio"), 0, 1);
        Invoke(window, "StopSpringRenderLoop");
        Invoke(window, "ProgressBar_MouseEnter", window, MouseArgs(Mouse.MouseEnterEvent));
        Assert.True(Field<bool>(window, "_isProgressBarExpanded"));
        Invoke(window, "ProgressBar_MouseLeave", window, MouseArgs(Mouse.MouseLeaveEvent));
        Assert.False(Field<bool>(window, "_isProgressBarExpanded"));
        await WpfFrameWaiter.NextAsync(ct);
    });

    [Theory]
    [InlineData(false, false, false, false, .9, 29.78)]
    [InlineData(true, false, false, false, .9, 29.1)]
    [InlineData(false, true, false, false, .9, 30)]
    [InlineData(true, true, false, false, .9, 30)]
    [InlineData(true, true, false, false, .4, 29.6)]
    [InlineData(false, true, true, false, .9, 29.1)]
    [InlineData(false, false, false, true, .9, 30)]
    public void SmallBackwardReportsRespectPlaybackAndUserSeekGuards(bool browser, bool playing, bool userSeek, bool postSeek, double backward, double expectedSeconds) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        Prepare(window, 30 - backward, playing, browser);
        Field<ProgressEngine>(window, "_progressEngine").GetUiFrame();
        Set(window, "_progressDisplayRatio", .3);
        Set(window, "_suppressExternalSeekDetectionUntil", DateTime.Now.AddSeconds(10));
        Set(window, "_allowProgressBackwardRenderUntil", userSeek ? DateTime.Now.AddSeconds(10) : DateTime.MinValue);
        Set(window, "_blockBackwardAfterSeekUntil", postSeek ? DateTime.Now.AddSeconds(10) : DateTime.MinValue);
        Invoke(window, "RenderProgressBar");
        Assert.InRange(window.ProgressBarScale.ScaleX * 100, expectedSeconds - .05, expectedSeconds + .05);
        Assert.InRange(window.ProgressBarScale.ScaleX, 0, 1);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(.302)]
    [InlineData(.7)]
    [InlineData(2)]
    public void ExternalSeekAndTrackRewindAnimationsFinishAtTheirTargets(double target) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        Prepare(window, 30, false, false);
        Invoke(window, "RenderProgressBar");
        Invoke(window, "AnimateExternalSeekTo", target);
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isRewindAnimating"), "external seek animation", ct);
        Assert.Equal(Math.Clamp(target, 0, 1), window.ProgressBarScale.ScaleX, 3);
        Assert.Equal(MediaProgressHelpers.FormatTime(TimeSpan.FromSeconds(Math.Clamp(target, 0, 1) * 100)), window.CurrentTimeText.Text);
        Invoke(window, "AnimateTrackChangeRewindToZero", Math.Clamp(target, 0, 1), TimeSpan.FromSeconds(200));
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isRewindAnimating"), "track change rewind", ct);
        Assert.Equal(0, window.ProgressBarScale.ScaleX);
        Assert.Equal("0:00", window.CurrentTimeText.Text);
        Assert.Equal("3:20", window.RemainingTimeText.Text);
        Invoke(window, "StartProgressCatchUpAnimation");
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(window.ProgressBarScale.ScaleX - .3) < .001, "progress catch up", ct);
        Invoke(window, "StartProgressCatchUpAnimation");
        Assert.Equal("0:30", window.CurrentTimeText.Text);
    });

    [Fact]
    public void ClickAndDragReleaseDispatchSeekAndClearPendingState() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        using var fixture = CreateFixture(media);
        var window = fixture.Window;
        Prepare(window, 30, false, false);
        window.ProgressBarContainer.Measure(new Size(300, 20));
        window.ProgressBarContainer.Arrange(new Rect(0, 0, 300, 20));
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Invoke(window, "ProgressBar_MouseDown", window, down);
        Assert.True(down.Handled);
        Assert.True(Field<bool>(window, "_isClickSeekPending"));
        Set(window, "_dragSeekPosition", TimeSpan.FromSeconds(60));
        Invoke(window, "ProgressBar_MouseUp", window, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        await WpfFrameWaiter.UntilAsync(() => media.LastSeek == TimeSpan.FromSeconds(60), "click seek dispatched", ct);
        Assert.False(Field<bool>(window, "_isClickSeekPending"));
        Set(window, "_isDraggingProgress", true);
        Set(window, "_dragSeekPosition", TimeSpan.FromSeconds(120));
        Invoke(window, "ProgressBar_MouseUp", window, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        await WpfFrameWaiter.UntilAsync(() => media.LastSeek == TimeSpan.FromSeconds(98.5), "drag seek dispatched", ct);
        Assert.False(Field<bool>(window, "_isDraggingProgress"));
        Invoke(window, "StopSpringRenderLoop");
    });

    [Fact]
    public void MissingTrackAndTimelineResetProgressWithoutSeeking() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var media = new FakeMediaDetectionService();
        using var fixture = CreateFixture(media);
        var window = fixture.Window;
        Prepare(window, 30, false, false);
        Invoke(window, "RenderProgressBar");
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = "No media playing" });
        Invoke(window, "RenderProgressBar");
        Assert.Equal(0, window.ProgressBarScale.ScaleX);
        Assert.Equal("0:00", window.CurrentTimeText.Text);
        Field<ProgressEngine>(window, "_progressEngine").Reset();
        await (Task)Invoke(window, "SeekToPosition", TimeSpan.FromSeconds(10))!;
        Assert.Null(media.LastSeek);
        Assert.Equal(TimeSpan.Zero, Invoke(window, "GetPositionForRatio", .5));
        Invoke(window, "ResetProgressUI");
        Assert.Equal(Visibility.Collapsed, window.IndeterminateProgress.Visibility);
        await WpfFrameWaiter.NextAsync(ct);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimelineUpdatesRejectStalePositionsAndPreserveArtistOnlyCorrections(bool expanded) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        Field<VNotch.Controllers.NotchTransitionCoordinator>(window, "_transitionCoordinator").ForceState(
            expanded ? NotchView.Media : NotchView.Compact,
            expanded ? NotchShapeState.Expanded : NotchShapeState.Collapsed, "progress fixture");
        var info = new MediaInfo
        {
            CurrentTrack = "Track",
            CurrentArtist = "Unknown",
            SourceAppId = "player",
            SessionInstanceKey = "session-a",
            Position = TimeSpan.FromSeconds(30),
            Duration = TimeSpan.FromSeconds(100),
            IsPlaying = true,
            IsAnyMediaPlaying = true,
            IsSeekEnabled = true,
            LastUpdated = DateTimeOffset.UtcNow
        };
        Invoke(window, "UpdateProgressTracking", info);
        Assert.Equal("0:30", window.CurrentTimeText.Text);
        long generation = Field<long>(window, "_trackChangeSequence");
        info.CurrentArtist = "Resolved artist";
        info.LastUpdated = DateTimeOffset.UtcNow;
        Invoke(window, "UpdateProgressTracking", info);
        Assert.Equal(generation, Field<long>(window, "_trackChangeSequence"));
        var acceptedTimestamp = Field<DateTimeOffset>(window, "_lastProgressTimelineUpdated");
        info.Position = TimeSpan.FromSeconds(10);
        info.LastUpdated = acceptedTimestamp.AddSeconds(-5);
        Invoke(window, "UpdateProgressTracking", info);
        Assert.Equal(acceptedTimestamp, Field<DateTimeOffset>(window, "_lastProgressTimelineUpdated"));
        Assert.InRange(Field<ProgressEngine>(window, "_progressEngine").GetUiFrame().Position.TotalSeconds, 29, 35);
        info.SessionInstanceKey = "session-b";
        info.LastUpdated = DateTimeOffset.UtcNow;
        Invoke(window, "UpdateProgressTracking", info);
        Assert.Equal("session-b", Field<string>(window, "_lastProgressSessionInstanceKey"));
        Assert.Equal(generation, Field<long>(window, "_trackChangeSequence"));
        info.CurrentTrack = "Next track";
        info.Position = TimeSpan.Zero;
        info.LastUpdated = DateTimeOffset.UtcNow;
        Invoke(window, "UpdateProgressTracking", info);
        Assert.Equal(generation + 1, Field<long>(window, "_trackChangeSequence"));
        await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isRewindAnimating"), "new track rewind", ct);
        Assert.InRange(window.ProgressBarScale.ScaleX, 0, .1);
        Assert.Equal("1:40", window.RemainingTimeText.Text);
    });

    [Theory]
    [InlineData(true, "LIVE")]
    [InlineData(false, "0:00")]
    public void UnknownDurationDistinguishesLivePlaybackFromPausedMedia(bool playing, string expected) => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        Set(window, "_currentMediaInfo", new MediaInfo { CurrentTrack = "Live program" });
        Field<ProgressEngine>(window, "_progressEngine").OnMediaSnapshot(new ProgressSnapshot
        {
            Position = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            IsPlaying = playing,
            Timestamp = DateTime.UtcNow,
            SequenceNumber = 1
        });
        Invoke(window, "RenderProgressBar");
        Assert.Equal(expected, window.RemainingTimeText.Text);
        Assert.Equal(0, window.ProgressBarScale.ScaleX);
    });

    [Theory]
    [InlineData(30, 50, true)]
    [InlineData(30, 20, true)]
    [InlineData(60, 0, false)]
    [InlineData(70, 10, false)]
    public void PlaybackJumpsAnimateOnlyPlausibleExternalSeeks(double displayed, double reported, bool animate) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture(new FakeMediaDetectionService());
        var window = fixture.Window;
        Prepare(window, reported, true, false);
        Field<ProgressEngine>(window, "_progressEngine").GetUiFrame();
        Set(window, "_progressDisplayRatio", displayed / 100);
        window.ProgressBarScale.ScaleX = displayed / 100;
        Invoke(window, "RenderProgressBar");
        Assert.Equal(animate, Field<bool>(window, "_isRewindAnimating"));
        if (animate)
        {
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_isRewindAnimating"), "external timeline seek", ct);
            Assert.InRange(window.ProgressBarScale.ScaleX, reported / 100, reported / 100 + .02);
        }
        else Assert.Equal(displayed / 100, window.ProgressBarScale.ScaleX);
    });

    private static void Prepare(MainWindow window, double seconds, bool playing, bool browser)
    {
        var info = new MediaInfo
        {
            CurrentTrack = "Test track",
            CurrentArtist = "Test artist",
            SourceAppId = browser ? "chrome" : "test-player",
            MediaSource = browser ? "YouTube" : "Spotify",
            Duration = TimeSpan.FromSeconds(100),
            Position = TimeSpan.FromSeconds(seconds),
            IsSeekEnabled = true,
            IsPlaying = playing,
            IsAnyMediaPlaying = playing
        };
        Set(window, "_currentMediaInfo", info);
        var engine = Field<ProgressEngine>(window, "_progressEngine");
        engine.Reset();
        engine.OnMediaSnapshot(new ProgressSnapshot
        {
            Position = info.Position,
            Duration = info.Duration,
            IsPlaying = playing,
            IsYouTube = browser,
            IsSeekEnabled = true,
            PlaybackRate = 1,
            Timestamp = DateTime.UtcNow,
            SequenceNumber = 1
        });
    }
    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture(FakeMediaDetectionService media) => new("en", greeting: false,
        configureSettings: settings => settings.EnableLocalOnlyMode = true,
        configureServices: services => services.AddSingleton<IMediaDetectionService>(media));
    private static MouseEventArgs MouseArgs(RoutedEvent routedEvent) => new(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent };
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
