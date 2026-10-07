using System.Runtime.InteropServices;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardRetryTests
{
    [Fact]
    public async Task TransientContentionRecoversAfterTheOldRetryWindow()
    {
        int attempts = 0, elapsed = 0;
        await ClipboardRetry.RunAsync(() =>
        {
            attempts++;
            if (elapsed < 700) throw new ExternalException("Busy");
        }, () => { }, ms => { elapsed += ms; return Task.CompletedTask; });
        Assert.True(attempts > 1);
        Assert.InRange(elapsed, 700, 2000);
    }

    [Fact]
    public async Task PermanentContentionIsBounded()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<ExternalException>(() => ClipboardRetry.RunAsync(() =>
        {
            attempts++;
            throw new ExternalException("Busy");
        }, () => { }, _ => Task.CompletedTask));
        Assert.Equal(ClipboardRetry.RetryCount + 1, attempts);
    }

    [Fact]
    public async Task LockingDuringRetryPreventsAnotherWrite()
    {
        bool locked = false;
        int attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardRetry.RunAsync(() =>
        {
            attempts++;
            throw new ExternalException("Busy");
        }, () => { if (locked) throw new InvalidOperationException("Locked"); },
        _ => { locked = true; return Task.CompletedTask; }));
        Assert.Equal(1, attempts);
    }
}
