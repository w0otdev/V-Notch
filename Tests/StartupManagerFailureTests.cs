using System.ComponentModel;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class StartupManagerFailureTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(0, 2)]
    public void RegistryErrorsPropagateToTheSettingsCaller(int status, int error)
    {
        var failure = Assert.Throws<Win32Exception>(() => StartupManager.EnsureChangeSucceeded(status, error));
        Assert.Equal(error, failure.NativeErrorCode);
    }

    [Fact]
    public void NativeFailureWithoutAWin32CodeStillFailsTheOperation() =>
        Assert.Throws<InvalidOperationException>(() => StartupManager.EnsureChangeSucceeded(-3, 0));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void InvalidNativeRequestsFailBeforeTouchingStartupRegistration(byte enable, int length)
    {
        Assert.NotEqual(0, SetEnabled(enable, 0, length, out int error));
        Assert.Equal(87, error);
    }

    [DllImport("VNotch.Native.dll", EntryPoint = "vn_startup_set_enabled")]
    private static extern int SetEnabled(byte enable, nint path, int length, out int error);
}
