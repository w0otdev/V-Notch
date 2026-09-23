using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class CoalescingActionQueueTests
{
    [Fact]
    public void SlowDeviceDoesNotBlockCallerAndPendingSliderWritesKeepLatestValue()
    {
        var queue = new CoalescingActionQueue();
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var writes = new List<int>();
        int caller = Environment.CurrentManagedThreadId;
        int worker = caller;
        bool released = false;
        queue.Post("slow-device", () =>
        {
            worker = Environment.CurrentManagedThreadId;
            entered.Set();
            released = unblock.Wait(TimeSpan.FromSeconds(5));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 1000; i++)
            {
                int value = i;
                queue.Post("volume", () => writes.Add(value));
            }
            queue.Post("other-device", () => writes.Add(1000));
            queue.Post("done", finished.Set);
            Assert.Empty(writes);
            Assert.NotEqual(caller, worker);
        }
        finally { unblock.Set(); }
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(released);
        Assert.Equal(new[] { 999, 1000 }, writes);
    }

    [Fact]
    public void FailedOperationDoesNotStrandSubsequentOperations()
    {
        var queue = new CoalescingActionQueue();
        using var finished = new ManualResetEventSlim();
        queue.Post("failure", () => throw new InvalidOperationException("Device disconnected"));
        queue.Post("next", finished.Set);
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
    }
}
