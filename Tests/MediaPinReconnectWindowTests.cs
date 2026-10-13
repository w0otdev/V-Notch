using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaPinReconnectWindowTests
{
    [Fact]
    public void RepeatedMissingSnapshotsKeepOneDeadlineAndWakeAtExpiry()
    {
        var clock = new ManualClock();
        int refreshes = 0;
        using var window = new MediaPinReconnectWindow(() => refreshes++, clock);
        window.Start();
        clock.Advance(MediaPinReconnectWindow.GracePeriod - TimeSpan.FromMilliseconds(1));
        window.Start();
        Assert.Single(clock.Timers);
        Assert.True(window.IsWaiting);
        Assert.False(window.HasExpired);
        Assert.Equal(0, refreshes);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(window.HasExpired);
        Assert.Equal(1, refreshes);
        clock.Timers[0].FireQueuedCallback();
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void EarlyTimerCallbackReschedulesForTheRemainingGracePeriod()
    {
        var clock = new ManualClock();
        int refreshes = 0;
        using var window = new MediaPinReconnectWindow(() => refreshes++, clock);
        window.Start();
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Timers[0].FireQueuedCallback();
        Assert.Equal(0, refreshes);
        Assert.Equal(TimeSpan.FromSeconds(4).Ticks, clock.Timers[0].DueAt);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void ReturningSessionOrChangingPinCancelsQueuedExpiry()
    {
        var clock = new ManualClock();
        int refreshes = 0;
        using var window = new MediaPinReconnectWindow(() => refreshes++, clock);
        window.Start();
        var oldTimer = clock.Timers[0];
        window.Reset();
        clock.Advance(MediaPinReconnectWindow.GracePeriod);
        oldTimer.FireQueuedCallback();
        Assert.False(window.IsWaiting);
        Assert.False(window.HasExpired);
        Assert.True(oldTimer.Disposed);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void OldQueuedCallbackCannotExpireANewerGap()
    {
        var clock = new ManualClock();
        int refreshes = 0;
        using var window = new MediaPinReconnectWindow(() => refreshes++, clock);
        window.Start();
        var oldTimer = clock.Timers[0];
        clock.Advance(TimeSpan.FromSeconds(2));
        window.Reset();
        window.Start();
        clock.Advance(TimeSpan.FromSeconds(2));
        oldTimer.FireQueuedCallback();
        Assert.False(window.HasExpired);
        Assert.Equal(0, refreshes);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(window.HasExpired);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void DisposalCancelsPendingRefreshAndPreventsRestart()
    {
        var clock = new ManualClock();
        int refreshes = 0;
        using var window = new MediaPinReconnectWindow(() => refreshes++, clock);
        window.Start();
        window.Dispose();
        clock.Advance(MediaPinReconnectWindow.GracePeriod);
        clock.Timers[0].FireQueuedCallback();
        window.Start();
        Assert.False(window.IsWaiting);
        Assert.Single(clock.Timers);
        Assert.Equal(0, refreshes);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        internal List<ManualTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            Timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (var timer in Timers.ToArray())
                if (!timer.Disposed && timer.DueAt <= _timestamp) timer.FireQueuedCallback();
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal long DueAt { get; private set; }
        internal bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            DueAt = clock.GetTimestamp() + dueTime.Ticks;
            return !Disposed;
        }
        // Timer disposal cannot retract a callback already queued by the OS.
        internal void FireQueuedCallback() => callback(state);
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
