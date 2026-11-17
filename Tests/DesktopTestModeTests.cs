using Xunit;

namespace VNotch.Tests;

public sealed class DesktopTestModeTests
{
    [Theory]
    [InlineData(true, "1", "true", "github-hosted", "Windows", true)]
    [InlineData(false, "1", "true", "github-hosted", "Windows", false)]
    [InlineData(true, null, "true", "github-hosted", "Windows", false)]
    [InlineData(true, "0", "true", "github-hosted", "Windows", false)]
    [InlineData(true, "1", null, null, null, false)]
    [InlineData(false, "1", null, null, null, false)]
    [InlineData(true, "1", "true", "self-hosted", "Windows", false)]
    [InlineData(true, "1", "true", null, "Windows", false)]
    [InlineData(true, "1", null, "github-hosted", "Windows", false)]
    [InlineData(true, "1", "true", "github-hosted", "Linux", false)]
    public void VisibleTestsRequireAnOptedInDisposableWindowsDesktop(bool interactive, string? optIn,
        string? actions, string? runner, string? os, bool expected) =>
        Assert.Equal(expected, DesktopTestMode.IsEnabled(interactive, optIn, actions, runner, os));
}
