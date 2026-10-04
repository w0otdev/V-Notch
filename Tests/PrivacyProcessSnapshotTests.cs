using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyProcessSnapshotTests
{
    [Fact]
    public void CaptureContainsCurrentExecutableAndProcessId()
    {
        var snapshot = PrivacyProcessSnapshot.Capture();
        string executableName = Path.GetFileName(Environment.ProcessPath!);

        Assert.True(snapshot.TryGetValue(executableName, out var processIds),
            $"Process snapshot did not contain {executableName}.");
        Assert.Contains((uint)Environment.ProcessId, processIds!);
    }
}
