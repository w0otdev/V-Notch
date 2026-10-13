using System.Reflection;
using System.Windows.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UiScheduledTimerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SchedulesShareOneWakeUpAndLeaveNoTimerRunningWhenStopped() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var first = new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(10) };
        using var second = new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(15) };
        var firstTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Tick += (_, _) => { first.Stop(); firstTick.TrySetResult(); };
        second.Tick += (_, _) => { second.Stop(); secondTick.TrySetResult(); };
        var scheduler = typeof(UiScheduledTimer).GetField("_scheduler", Private)!.GetValue(first)!;
        Assert.Same(scheduler, typeof(UiScheduledTimer).GetField("_scheduler", Private)!.GetValue(second));
        first.Start();
        second.Start();
        await Task.WhenAll(firstTick.Task, secondTick.Task).WaitAsync(ct);
        var wakeUp = (DispatcherTimer)scheduler.GetType().GetField("_wakeUp", Private)!.GetValue(scheduler)!;
        Assert.False(wakeUp.IsEnabled);
        Assert.False(first.IsEnabled);
        Assert.False(second.IsEnabled);
    });

    [Fact]
    public void StopRestartAndIntervalChangesInvalidateQueuedCallbacks() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var timer = new UiScheduledTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(1) };
        int ticks = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        timer.Tick += (_, _) => { ticks++; timer.Stop(); completed.TrySetResult(); };
        timer.Start();
        timer.DispatchTick();
        timer.Stop();
        timer.Start();
        await timer.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(ct);
        Assert.Equal(0, ticks);
        timer.DispatchTick();
        timer.Interval = TimeSpan.FromMilliseconds(10);
        await completed.Task.WaitAsync(ct);
        await timer.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(ct);
        Assert.Equal(1, ticks);
    });

    [Fact]
    public void DisposingCancelsAlreadyQueuedWork() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var timer = new UiScheduledTimer { Interval = TimeSpan.FromHours(1) };
        int ticks = 0;
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        timer.DispatchTick();
        timer.Dispose();
        await timer.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(ct);
        Assert.Equal(0, ticks);
        Assert.Throws<ObjectDisposedException>(timer.Start);
    });
}
