using System;
using System.Diagnostics;
using VNotch.Controllers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public class CountdownTrackerTests
{
    [Fact]
    public void AdvanceCountdown_Simulated2SecondDelay_DecreasesRemainingBy2Seconds()
    {
        // Arrange
        var timer = new TimerViewModel();
        timer.SetCustomDuration(TimeSpan.FromSeconds(10));
        var tracker = new CountdownTracker(timer);

        long t0 = Stopwatch.GetTimestamp();
        timer.Start();
        tracker.ResetCountdownTimestamp(t0);

        // Advance normal 100ms
        long t1 = t0 + (long)(0.1 * Stopwatch.Frequency);
        bool finished1 = tracker.AdvanceCountdown(t1);
        Assert.False(finished1);
        Assert.Equal(TimeSpan.FromMilliseconds(9900), timer.Remaining);

        // Simulate UI dispatcher delay of 2 seconds
        long t2 = t1 + (2 * Stopwatch.Frequency);
        bool finished2 = tracker.AdvanceCountdown(t2);

        // Assert - remaining must decrease by 2 seconds (to 7.9s)
        Assert.False(finished2);
        Assert.Equal(TimeSpan.FromMilliseconds(7900), timer.Remaining);
    }

    [Fact]
    public void PauseAndResume_DoesNotCountPausedInterval_AndPreservesPrePauseElapsed()
    {
        // Arrange
        var timer = new TimerViewModel();
        timer.SetCustomDuration(TimeSpan.FromSeconds(10));
        var tracker = new CountdownTracker(timer);

        long t0 = Stopwatch.GetTimestamp();
        timer.Start();
        tracker.ResetCountdownTimestamp(t0);

        // Run for 400ms
        long t1 = t0 + (long)(0.4 * Stopwatch.Frequency);
        tracker.AdvanceCountdown(t1);
        Assert.Equal(TimeSpan.FromMilliseconds(9600), timer.Remaining);

        // 250ms later, user pauses (commits elapsed before pause)
        long tPause = t1 + (long)(0.25 * Stopwatch.Frequency);
        bool finishedPause = tracker.Pause(tPause);

        Assert.False(finishedPause);
        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(9350), timer.Remaining);

        // System stays paused for 5 seconds
        long tResume = tPause + (5 * Stopwatch.Frequency);

        // Resume at tResume
        tracker.Start(tResume);
        Assert.True(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(9350), timer.Remaining);

        // First tick 100ms after resume
        long tTickAfterResume = tResume + (long)(0.1 * Stopwatch.Frequency);
        bool finishedResumeTick = tracker.AdvanceCountdown(tTickAfterResume);

        // Assert: Remaining should decrease by 100ms from 9350ms to 9250ms.
        // The 5 seconds paused interval was completely excluded!
        Assert.False(finishedResumeTick);
        Assert.Equal(TimeSpan.FromMilliseconds(9250), timer.Remaining);
    }

    [Fact]
    public void AdvanceCountdown_ElapsedExceedsRemaining_CompletesCountdown()
    {
        // Arrange
        var timer = new TimerViewModel();
        timer.SetCustomDuration(TimeSpan.FromSeconds(5));
        var tracker = new CountdownTracker(timer);

        long t0 = Stopwatch.GetTimestamp();
        timer.Start();
        tracker.ResetCountdownTimestamp(t0);

        // Simulate 6 seconds delay when only 5 seconds remain
        long t1 = t0 + (6 * Stopwatch.Frequency);
        bool finished = tracker.AdvanceCountdown(t1);

        // Assert
        Assert.True(finished);
        Assert.Equal(TimeSpan.Zero, timer.Remaining);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void Pause_WhenElapsedExceedsRemaining_CompletesCountdown()
    {
        // Arrange
        var timer = new TimerViewModel();
        timer.SetCustomDuration(TimeSpan.FromSeconds(5));
        var tracker = new CountdownTracker(timer);

        long t0 = Stopwatch.GetTimestamp();
        timer.Start();
        tracker.ResetCountdownTimestamp(t0);

        // 6 seconds elapsed before pause clicked
        long tPause = t0 + (6 * Stopwatch.Frequency);
        bool finished = tracker.Pause(tPause);

        // Assert: countdown completed
        Assert.True(finished);
        Assert.Equal(TimeSpan.Zero, timer.Remaining);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void AdvanceCountdown_WithoutPriorReset_SafelyInitializesWithoutJumping()
    {
        // Arrange
        var timer = new TimerViewModel();
        timer.SetCustomDuration(TimeSpan.FromSeconds(5));
        timer.Start();
        var tracker = new CountdownTracker(timer);

        long t0 = Stopwatch.GetTimestamp();
        // Advance before ResetCountdownTimestamp was called
        bool finished = tracker.AdvanceCountdown(t0);

        // Should initialize timestamp and not decrease remaining
        Assert.False(finished);
        Assert.Equal(TimeSpan.FromSeconds(5), timer.Remaining);
        Assert.Equal(t0, tracker.LastCountdownTimestamp);
    }

    [Fact]
    public void AdvanceCountdown_WhenNotRunning_DoesNotDecreaseRemaining()
    {
        // Arrange
        var timer = new TimerViewModel();
        timer.SetCustomDuration(TimeSpan.FromSeconds(10));
        var tracker = new CountdownTracker(timer);

        long t0 = Stopwatch.GetTimestamp();
        tracker.ResetCountdownTimestamp(t0);

        // Advance while timer is not started/paused
        long t1 = t0 + (2 * Stopwatch.Frequency);
        bool finished = tracker.AdvanceCountdown(t1);

        Assert.False(finished);
        Assert.Equal(TimeSpan.FromSeconds(10), timer.Remaining);
    }
}
