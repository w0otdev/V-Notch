using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MediaProgressNavigationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(.2, .4)]
    [InlineData(.6, .4)]
    public void CompletedSeek_IsNotReplayedWhenPlayerIsHiddenAndShown(double initialRatio, double targetRatio) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var view = new MediaPlaybackView();
        var host = new Window { Content = view, Width = 400, Height = 200, ShowInTaskbar = false };
        try
        {
            host.Show();
            view.UpdateProgressTracking(Info(initialRatio));
            // An earlier local seek leaves a settled spring behind. The next
            // external seek must remain authoritative across visibility changes.
            view.AnimateSeekProgressTo(initialRatio);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(view, "_isSeekSpringActive"), "initial seek settled", ct);
            view.Progress.OnMediaSnapshot(Snapshot(targetRatio, 2));
            view.AnimateExternalSeekTo(targetRatio);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(view, "_isRewindAnimating"), "external seek settled", ct);
            Assert.InRange(view.DisplayRatio, targetRatio, targetRatio + .02);
            await WpfFrameWaiter.UntilAsync(() => DateTime.UtcNow >= Field<DateTime>(view, "_suppressExternalSeekDetectionUntil"),
                "seek stabilization expired", ct);
            double expectedRatio = view.Progress.GetUiFrame().Position.TotalSeconds / 100;

            view.Visibility = Visibility.Collapsed;
            view.Visibility = Visibility.Visible;
            view.StartProgressCatchUpAnimation();

            Assert.InRange(view.DisplayRatio, expectedRatio - .005, expectedRatio + .005);
            Assert.False(Field<bool>(view, "_isRewindAnimating"));
            Assert.False(Field<bool>(view, "_isCatchUpAnimating"));
            Assert.False(view.ProgressBarScale.HasAnimatedProperties);
            await Task.Delay(60, ct);
            Assert.InRange(view.DisplayRatio, expectedRatio, expectedRatio + .02);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData(.2, .4)]
    [InlineData(.6, .4)]
    public void CompletedSeek_IsNotReplayedByNotchCollapseAndExpand(double initialRatio, double targetRatio) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previousReducedMotion = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            await using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", false,
                s => { s.EnableLocalOnlyMode = true; s.DisableMouseLeaveAutoClose = true; },
                services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
            var window = fixture.Window;
            var view = window.MediaPlayback;
            var coordinator = (NotchTransitionCoordinator)typeof(MainWindow).GetField("_transitionCoordinator", Private)!.GetValue(window)!;
            window.Show();
            Assert.True(coordinator.RequestView(NotchView.Media, "OpenForSeek"));
            await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "initial expansion", ct);
            view.UpdateProgressTracking(Info(initialRatio));
            view.AnimateSeekProgressTo(initialRatio);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(view, "_isSeekSpringActive"), "initial seek settled", ct);
            view.Progress.OnMediaSnapshot(Snapshot(targetRatio, 2));
            view.AnimateExternalSeekTo(targetRatio);
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(view, "_isRewindAnimating") &&
                DateTime.UtcNow >= Field<DateTime>(view, "_suppressExternalSeekDetectionUntil"), "seek stabilized", ct);

            for (int cycle = 0; cycle < 2; cycle++)
            {
                Assert.True(coordinator.RequestView(NotchView.Compact, "CloseAfterSeek"));
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "notch collapsed", ct);
                Assert.True(coordinator.RequestView(NotchView.Media, "ReopenAfterSeek"));
                await WpfFrameWaiter.UntilAsync(() => !coordinator.IsTransitionActive, "notch reopened", ct);

                double expectedRatio = view.Progress.GetUiFrame().Position.TotalSeconds / 100;
                Assert.InRange(view.DisplayRatio, expectedRatio - .005, expectedRatio + .005);
                Assert.False(Field<bool>(view, "_isRewindAnimating"));
                Assert.False(Field<bool>(view, "_isCatchUpAnimating"));
                Assert.False(view.ProgressBarScale.HasAnimatedProperties);
            }

            double nextTarget = targetRatio + .15;
            view.Progress.OnMediaSnapshot(Snapshot(nextTarget, 3));
            view.RenderProgressBar();
            Assert.True(Field<bool>(view, "_isRewindAnimating"));
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(view, "_isRewindAnimating"), "new seek after reopening", ct);
            Assert.InRange(view.DisplayRatio, nextTarget, nextTarget + .02);
        }
        finally { AnimationConfig.SetReduceMotion(previousReducedMotion); }
    });

    private static MediaInfo Info(double ratio) => new()
    {
        CurrentTrack = "Track",
        CurrentArtist = "Artist",
        SourceAppId = "player",
        Duration = TimeSpan.FromSeconds(100),
        Position = TimeSpan.FromSeconds(ratio * 100),
        IsPlaying = true,
        IsAnyMediaPlaying = true,
        IsSeekEnabled = true
    };

    private static ProgressSnapshot Snapshot(double ratio, long sequence) => new()
    {
        Duration = TimeSpan.FromSeconds(100),
        Position = TimeSpan.FromSeconds(ratio * 100),
        IsPlaying = true,
        IsSeekEnabled = true,
        PlaybackRate = 1,
        Timestamp = DateTime.UtcNow,
        SequenceNumber = sequence
    };

    private static T Field<T>(MediaPlaybackView view, string name) =>
        (T)typeof(MediaPlaybackView).GetField(name, Private)!.GetValue(view)!;
}
