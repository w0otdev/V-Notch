using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("2.0.1-beta.184.1+build.1234567890.1.sha.abc", "2.0.1-beta.184.1")]
    [InlineData("2.0.0+build.1234567890.1.sha.abc", "2.0.0")]
    [InlineData("2.0.0", "2.0.0")]
    [InlineData(null, "2.0.0")]
    [InlineData("invalid", "2.0.0")]
    public void ReleaseIdentityUsesInformationalVersionWithoutBuildMetadata(string? informational, string expected)
        => Assert.Equal(expected, AppVersion.Resolve(new Version(2, 0, 0, 0), informational));

    [Fact]
    public void AllReleaseConsumersUseSameVersion()
    {
        using var service = new UpdateService();
        Assert.Equal(AppVersion.Current, service.CurrentVersion);
        Assert.Equal(AppVersion.Current, AppIntegrityService.GetAppVersion());
    }
}
