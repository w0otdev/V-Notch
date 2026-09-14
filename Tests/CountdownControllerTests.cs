using System;
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
}
