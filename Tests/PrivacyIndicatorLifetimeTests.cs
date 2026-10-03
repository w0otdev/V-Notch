using System.Reflection;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyIndicatorLifetimeTests
{
    [Theory]
    [InlineData("_scanGate")]
    [InlineData("_micFlowGate")]
    public async Task DisposalDefersGateReleaseUntilAnInFlightWorkerFinishes(string gateName)
    {
        var service = new PrivacyIndicatorService();
        var gate = Get<SemaphoreSlim>(service, gateName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold the actual production gate across disposal, like a scan/probe
        // paused inside its critical section. Register it in the worker lifetime.
        var worker = Task.Run(async () =>
        {
            await gate.WaitAsync();
            entered.SetResult();
            try { await finish.Task; }
            finally { gate.Release(); }
        });
        lock (Get<object>(service, "_lifecycleLock")) Get<HashSet<Task>>(service, "_workers").Add(worker);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            service.Dispose();
            Assert.False(service.CleanupCompletion.IsCompleted);
            finish.SetResult();
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            await service.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
        }
        finally
        {
            finish.TrySetResult();
            service.Dispose();
            await service.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RapidRestartAndDisposeWaitsForEveryWorkerGeneration()
    {
        for (int i = 0; i < 10; i++)
        {
            var service = new PrivacyIndicatorService(TimeSpan.FromMilliseconds(5));
            service.Start();
            await Task.Delay(5);
            service.Stop();
            service.Start();
            await Task.Delay(5);
            service.Dispose();
            await service.CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            service.Dispose();
            service.Start();
            Assert.Empty(Get<HashSet<Task>>(service, "_workers"));
        }
    }

    private static T Get<T>(PrivacyIndicatorService service, string name) =>
        (T)typeof(PrivacyIndicatorService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
}
