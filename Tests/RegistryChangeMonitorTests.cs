using Microsoft.Win32;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class RegistryChangeMonitorTests
{
    [Fact]
    public void MissingSubscriptionDoesNotRequestAFullScanOnEveryPoll()
    {
        long now = 0;
        using var monitor = new RegistryChangeMonitor(Registry.CurrentUser,
            @"Software\VNotch.Tests\Missing-" + Guid.NewGuid().ToString("N"), () => now);

        Assert.True(monitor.ConsumeChange());
        now = 1_000;
        Assert.False(monitor.ConsumeChange());
        now = 29_999;
        Assert.False(monitor.ConsumeChange());
        now = 30_000;
        Assert.True(monitor.ConsumeChange());
        now = 31_000;
        Assert.False(monitor.ConsumeChange());
    }
}
