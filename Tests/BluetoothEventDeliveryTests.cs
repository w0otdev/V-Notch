using System.Reflection;
using System.Windows.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class BluetoothEventDeliveryTests
{
    [Fact]
    public void RapidChangesAcrossDevicesAreDeliveredInOrderOnTheDispatcher() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var service = new BluetoothMonitorService();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var received = new List<string>();
        service.DeviceConnected += (_, info) => { Assert.True(dispatcher.CheckAccess()); received.Add("+" + info.Id); };
        service.DeviceDisconnected += (_, info) => { Assert.True(dispatcher.CheckAccess()); received.Add("-" + info.Id); };
        service.AddDevice(new() { Id = "A", Name = "Headphones" });
        CompleteEnumeration(service);
        await Task.Run(() =>
        {
            service.RemoveDevice("A");
            service.AddDevice(new() { Id = "B", Name = "Speaker" });
            service.AddDevice(new() { Id = "B", Name = "Duplicate" });
            service.RemoveDevice("B");
            service.RemoveDevice("B");
        });
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(new[] { "-A", "+B", "-B" }, received);
    });

    [Fact]
    public void DisposalSuppressesNotificationsAlreadyQueuedByAWorker() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var service = new BluetoothMonitorService();
        CompleteEnumeration(service);
        int received = 0;
        service.DeviceConnected += (_, _) => received++;
        Task.Run(() => service.AddDevice(new() { Id = "A", Name = "Headphones" })).GetAwaiter().GetResult();
        service.Dispose();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, received);
    });

    private static void CompleteEnumeration(BluetoothMonitorService service) =>
        typeof(BluetoothMonitorService).GetField("_isInitialEnumerationComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, true);

    [Fact]
    public void StoppingSuppressesNotificationsAlreadyQueuedByAWorker() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var service = new BluetoothMonitorService();
        CompleteEnumeration(service);
        int received = 0;
        service.DeviceConnected += (_, _) => received++;
        Task.Run(() => service.AddDevice(new() { Id = "A", Name = "Headphones" })).GetAwaiter().GetResult();
        service.Stop();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, received);
    });

    [Fact]
    public void AStaleEnumerationCallbackCannotReactivateAStoppedMonitor() => SharedStaTestRunner.Run(() =>
    {
        using var service = new BluetoothMonitorService();
        CompleteEnumeration(service);
        service.Stop();
        typeof(BluetoothMonitorService).GetMethod("Watcher_EnumerationCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [null, new object()]);
        Assert.False((bool)typeof(BluetoothMonitorService).GetField("_isInitialEnumerationComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!);
    });

    [Fact]
    public void NullStoppedCallbackCannotStartAStoppedMonitor() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var service = new BluetoothMonitorService();
        typeof(BluetoothMonitorService).GetMethod("Watcher_Stopped", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [null, new object()]);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Null(typeof(BluetoothMonitorService).GetField("_watcher", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
    });

    [Fact]
    public void UnexpectedStopsBackOffExponentiallyAndStopAtCap() => SharedStaTestRunner.RunAsync(async () =>
    {
        var recordedDelays = new List<TimeSpan>();
        int restarts = 0;
        var dispatcher = Dispatcher.CurrentDispatcher;

        using var service = new BluetoothMonitorService(
            dispatcher,
            (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                recordedDelays.Add(delay);
                return Task.CompletedTask;
            },
            () => restarts++);

        for (int i = 0; i < BluetoothMonitorService.MaxConsecutiveRestarts; i++)
        {
            Assert.True(service.TryScheduleRestartForTest());
            await Task.Delay(15);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        Assert.False(service.TryScheduleRestartForTest());
        Assert.Equal(BluetoothMonitorService.MaxConsecutiveRestarts, restarts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(4000), TimeSpan.FromMilliseconds(8000)],
            recordedDelays);
        Assert.Equal(TimeSpan.FromMilliseconds(30_000), BluetoothMonitorService.ComputeRestartDelay(10));
    });

    [Fact]
    public void StopAndDisposeCancelPendingDelayedRestart() => SharedStaTestRunner.RunAsync(async () =>
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int restarts = 0;

        using var service = new BluetoothMonitorService(
            Dispatcher.CurrentDispatcher,
            async (_, ct) =>
            {
                delayStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { delayCanceled.TrySetResult(); throw; }
            },
            () => restarts++);

        Assert.True(service.TryScheduleRestartForTest());
        await delayStarted.Task;
        service.Dispose();
        await delayCanceled.Task;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, restarts);
    });
}
