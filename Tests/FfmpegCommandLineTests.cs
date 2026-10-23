using System.Diagnostics;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class FfmpegCommandLineTests
{
    [Fact]
    public void ReadCommandLineCanRepeatedlyReadCurrentProcessThroughExplicitWmiProperties()
    {
        using var process = Process.GetCurrentProcess();
        string? first = FfmpegRecordingProbe.ReadCommandLine(process.Id);
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Contains(process.ProcessName, first, StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < 3; i++)
            Assert.Equal(first, FfmpegRecordingProbe.ReadCommandLine(process.Id));
    }
}
