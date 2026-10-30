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
}
