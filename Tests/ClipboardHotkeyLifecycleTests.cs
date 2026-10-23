using System.Windows;
using System.Windows.Interop;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardHotkeyLifecycleTests
{
    [Fact]
    public void ControllersKeepIndependentErrorsAndDisposingOldHostDoesNotClearCurrentError() => SharedStaTestRunner.Run(() =>
    {
        var first = new Window();
        var second = new Window();
        new WindowInteropHelper(first).EnsureHandle();
        new WindowInteropHelper(second).EnsureHandle();
        using var oldController = new ClipboardHotkeyController(first, () => { });
        using var activeController = new ClipboardHotkeyController(second, () => { });
        try
        {
            oldController.Apply("Shift+A");
            activeController.Apply("Win+V");
            string? currentError = activeController.RegistrationError;
            Assert.NotNull(currentError);
            Assert.NotEqual(oldController.RegistrationError, currentError);
            oldController.Dispose();
            Assert.Equal(currentError, activeController.RegistrationError);
            activeController.Apply("");
            Assert.Null(activeController.RegistrationError);
            Assert.Throws<ObjectDisposedException>(() => oldController.Apply("Ctrl+V"));
        }
        finally { first.Close(); second.Close(); }
    });
}
