using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyProcessSnapshotTests
{
    [Fact]
    public void CaptureContainsCurrentExecutableAndProcessId()
    {
        using var snapshot = PrivacyProcessSnapshot.Capture();
        string executableName = Path.GetFileName(Environment.ProcessPath!);

        int current = Enumerable.Range(0, snapshot.Count)
            .Single(index => snapshot.GetProcessId(index) == (uint)Environment.ProcessId);
        Assert.True(snapshot.MatchesExecutableName(current, executableName));
        Assert.True(snapshot.MatchesExecutableName(current, executableName.ToUpperInvariant()));
        Assert.False(snapshot.MatchesExecutableName(current, executableName + ".other"));
    }

    [Fact]
    public void DisposeReturnsTheBufferAndDoesNotAffectAnotherSnapshot()
    {
        using var first = PrivacyProcessSnapshot.Capture();
        using var second = PrivacyProcessSnapshot.Capture();
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, first.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.GetProcessId(0));
        Assert.Contains((uint)Environment.ProcessId,
            Enumerable.Range(0, second.Count).Select(second.GetProcessId));
    }

    [Fact]
    public void ConsumerMatchingRequiresTheFullExecutablePathAndPreservesTheSnapshot()
    {
        using var snapshot = PrivacyProcessSnapshot.Capture();
        string executable = Environment.ProcessPath!;
        Assert.True(snapshot.ContainsConsumer(executable, package: false));
        Assert.True(snapshot.ContainsConsumer(executable.ToUpperInvariant(), package: false));
        Assert.False(snapshot.ContainsConsumer(executable + ".other", package: false));
        Assert.False(snapshot.ContainsConsumer(Path.Combine(Path.GetTempPath(), "not-the-app", Path.GetFileName(executable)), package: false));
        Assert.False(snapshot.ContainsConsumer("VNotch.NonexistentPackage_123456", package: true));
        Assert.Contains((uint)Environment.ProcessId,
            Enumerable.Range(0, snapshot.Count).Select(snapshot.GetProcessId));
    }
}
