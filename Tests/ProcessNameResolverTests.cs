using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Process handles")]
public sealed class ProcessNameResolverTests
{
    [Fact]
    public void ResolvesCurrentProcessAndRejectsMissingProcesses()
    {
        using var process = Process.GetCurrentProcess();
        Assert.Equal(process.ProcessName, ProcessNameResolver.TryGetName((uint)process.Id), ignoreCase: true);
        Assert.Null(ProcessNameResolver.TryGetName(0));
        Assert.Null(ProcessNameResolver.TryGetName(uint.MaxValue));
    }

    [Fact]
    public void RepeatedLookupsCloseNativeHandlesImmediately()
    {
        using var process = Process.GetCurrentProcess();
        uint id = (uint)process.Id;
        for (int i = 0; i < 20; i++) Assert.NotNull(ProcessNameResolver.TryGetName(id));
        process.Refresh();
        int before = process.HandleCount;
        for (int i = 0; i < 1000; i++) Assert.NotNull(ProcessNameResolver.TryGetName(id));
        process.Refresh();
        Assert.InRange(process.HandleCount - before, -int.MaxValue, 20);
    }
}

[CollectionDefinition("Process handles", DisableParallelization = true)]
public sealed class ProcessHandlesCollection { }
