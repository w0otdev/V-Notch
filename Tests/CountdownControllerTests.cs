using System;
using System.Threading.Channels;
using VNotch.Controllers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public class CountdownControllerTests
{
    [Fact]
    public void Start_AndAdvance_FiresTickEvent()
    {
        var vm = new TimerViewModel();
        vm.SetCustomDuration(TimeSpan.FromSeconds(5));

        var controller = new CountdownController(vm, action => action());
        int tickCount = 0;
        bool completedFired = false;
        controller.Tick += (_, _) => tickCount++;
        controller.Completed += (_, _) => completedFired = true;

        controller.Start();
        Assert.True(controller.IsRunning);

        long startTimestamp = controller.Tracker.LastCountdownTimestamp;
        // Advance time by 1 second
        controller.Advance(startTimestamp + System.Diagnostics.Stopwatch.Frequency);

        Assert.Equal(1, tickCount);
        Assert.True(controller.Remaining <= TimeSpan.FromSeconds(4));
        Assert.False(completedFired);

        // Advance past remaining duration
        controller.Advance(startTimestamp + System.Diagnostics.Stopwatch.Frequency * 6);

        Assert.True(tickCount >= 2);
        Assert.True(completedFired);
        Assert.False(controller.IsRunning);
        Assert.Equal(TimeSpan.Zero, controller.Remaining);

        controller.Dispose();
    }

    [Fact]
    public void Reset_StopsTimerAndResetsRemaining()
    {
        var vm = new TimerViewModel();
        vm.SetCustomDuration(TimeSpan.FromSeconds(10));

        var controller = new CountdownController(vm, action => action());
        controller.Start();
        Assert.True(controller.IsRunning);

        controller.Reset();
        Assert.False(controller.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(10), controller.Remaining);

        controller.Dispose();
    }

    [Fact]
    public void AdvanceRaisesTickAndCompletionWithoutDispatchingAgain()
    {
        var vm = new TimerViewModel();
        vm.SetCustomDuration(TimeSpan.FromSeconds(5));
        using var controller = new CountdownController(vm, _ => throw new InvalidOperationException("Unexpected dispatch"));
        int ticks = 0;
        int completions = 0;
        controller.Tick += (_, _) => ticks++;
        controller.Completed += (_, _) => completions++;
        controller.Start();
        long end = controller.Tracker.LastCountdownTimestamp + System.Diagnostics.Stopwatch.Frequency * 6;

        Assert.True(controller.Advance(end));
        Assert.False(controller.Advance(end));
        Assert.Equal(1, ticks);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task BusyUiCoalescesTicksAndUsesElapsedTime()
    {
        var pending = Channel.CreateUnbounded<Action>();
        var vm = new TimerViewModel();
        vm.SetCustomDuration(TimeSpan.FromSeconds(5));
        using var controller = new CountdownController(vm, action => pending.Writer.TryWrite(action));
        int ticks = 0;
        controller.Tick += (_, _) => { ticks++; controller.Pause(); };
        controller.Start();
        Action tick = await pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Leave the callback pending across several timer intervals.
        await Task.Delay(350);
        Assert.False(pending.Reader.TryRead(out _));
        Assert.Equal(TimeSpan.FromSeconds(5), controller.Remaining);
        tick();

        Assert.Equal(1, ticks);
        Assert.True(controller.Remaining < TimeSpan.FromSeconds(4.8));
        Assert.False(pending.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledQueuedTickCannotAdvanceANewRun(bool pauseAndResume)
    {
        var pending = Channel.CreateUnbounded<Action>();
        var vm = new TimerViewModel();
        vm.SetCustomDuration(TimeSpan.FromSeconds(5));
        using var controller = new CountdownController(vm, action => pending.Writer.TryWrite(action));
        int ticks = 0;
        controller.Tick += (_, _) => { ticks++; controller.Pause(); };
        controller.Start();
        Action staleTick = await pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (pauseAndResume)
        {
            controller.Pause();
            controller.Resume();
        }
        else
        {
            controller.Reset();
            controller.Start();
        }
        Action currentTick = await pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        TimeSpan before = controller.Remaining;

        staleTick();
        Assert.Equal(0, ticks);
        Assert.Equal(before, controller.Remaining);
        currentTick();
        Assert.Equal(1, ticks);
        Assert.False(pending.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisposingSkipsAQueuedTick()
    {
        var pending = Channel.CreateUnbounded<Action>();
        using var controller = new CountdownController(new TimerViewModel(), action => pending.Writer.TryWrite(action));
        int ticks = 0;
        controller.Tick += (_, _) => ticks++;
        controller.Start();
        Action tick = await pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        TimeSpan before = controller.Remaining;

        controller.Dispose();
        tick();
        Assert.Equal(0, ticks);
        Assert.Equal(before, controller.Remaining);
    }
}
