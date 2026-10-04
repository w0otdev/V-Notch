using System.Windows.Threading;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class DebouncerThreadTests
{
    [Fact]
    public void WorkerCallsExecuteOnlyTheLatestActionOnTheOwningDispatcher() => SharedStaTestRunner.RunAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var debouncer = new Debouncer(TimeSpan.FromMilliseconds(50));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool obsoleteRan = false;
        await Task.Run(() =>
        {
            debouncer.Debounce(() => obsoleteRan = true);
            debouncer.Debounce(() =>
            {
                Assert.True(dispatcher.CheckAccess());
                completion.SetResult();
            });
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(obsoleteRan);
    });

    [Fact]
    public void FlushPreservesAnActionScheduledReentrantly() => SharedStaTestRunner.Run(() =>
    {
        using var debouncer = new Debouncer(TimeSpan.FromSeconds(1));
        int count = 0;
        debouncer.Debounce(() =>
        {
            count++;
            debouncer.Debounce(() => count++);
        });
        debouncer.Flush();
        debouncer.Flush();
        Assert.Equal(2, count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerCancellationAndDisposalDiscardPendingActions(bool dispose) => SharedStaTestRunner.RunAsync(async () =>
    {
        using var debouncer = new Debouncer(TimeSpan.FromSeconds(1));
        bool ran = false;
        debouncer.Debounce(() => ran = true);
        await Task.Run(() => { if (dispose) debouncer.Dispose(); else debouncer.Cancel(); });
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        debouncer.Flush();
        Assert.False(ran);
    });
}
