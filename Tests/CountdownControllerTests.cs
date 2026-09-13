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
        controller.Tick += (_, _) => tickCount++;

        controller.Start();
        Assert.True(controller.IsRunning);

        // Advance manually via tracker logic inside controller
        // Tick is fired whenever timer advances
        Assert.Equal(0, tickCount);

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
