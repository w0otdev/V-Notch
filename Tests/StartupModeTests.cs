using VNotch;
using Xunit;

namespace VNotch.Tests;

public sealed class StartupModeTests
{
    [Theory]
    [InlineData("--setup")]
    [InlineData("--uninstall")]
    public void MaintenanceFlagsUseIsolatedStartup(string flag)
        => Assert.True(App.IsSetupOrUninstall([flag], "V-Notch.exe"));

    [Fact]
    public void SetupSourceAndInstallerNameUseIsolatedStartup()
    {
        Assert.True(App.IsSetupOrUninstall(["--setup-source", "payload"], "V-Notch.exe"));
        Assert.True(App.IsSetupOrUninstall([], "V-Notch-Setup.exe"));
    }

    [Fact]
    public void OrdinaryAndRestartLaunchesInitializeSynchronously()
    {
        Assert.False(App.IsSetupOrUninstall([], "V-Notch.exe"));
        Assert.False(App.IsSetupOrUninstall(["--restart"], "V-Notch.exe"));
        Assert.False(App.IsSetupOrUninstall(["--setup-source", "  "], "V-Notch.exe"));
    }
}
