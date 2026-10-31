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

    [Fact]
    public void MediaWindowActivator_MatchesResolvedProcessNameWithoutOpeningFullAccessProcessHandle()
    {
        using var process = Process.GetCurrentProcess();
        uint pid = (uint)process.Id;
        int processByIdCalls = 0;

        bool matched = MediaWindowActivator.IsMediaBrowserWindowCore(
            new IntPtr(0x1001),
            $"{process.ProcessName}.exe",
            pid,
            ProcessNameResolver.TryGetName,
            isChromiumWindow: static _ => true,
            getProcessById: _ =>
            {
                processByIdCalls++;
                throw new InvalidOperationException("Process.GetProcessById must not be invoked when ProcessNameResolver matches.");
            });

        Assert.True(matched);
        Assert.Equal(0, processByIdCalls);
    }
}

[CollectionDefinition("Process handles", DisableParallelization = true)]
public sealed class ProcessHandlesCollection { }
