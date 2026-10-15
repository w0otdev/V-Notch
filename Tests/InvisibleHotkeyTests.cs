using System.Windows;
using System.Windows.Interop;
using VNotch.Controllers;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class InvisibleHotkeyTests
{
    [Fact]
    public void HotkeyDispatchesOnceAndQueuedWorkStopsAfterDisposal() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var window = new Window { ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int toggles = 0;
        using var controller = new InvisibleHotkeyController(window, () => toggles++);
        try
        {
            var handler = typeof(InvisibleHotkeyController).GetMethod("WndProc",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            object[] args = [hwnd, Win32Interop.WM_HOTKEY, (IntPtr)InvisibleHotkeyController.HotkeyId, IntPtr.Zero, false];
            handler.Invoke(controller, args);
            Assert.True((bool)args[4]);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, toggles);
            handler.Invoke(controller, args);
            controller.Dispose();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, toggles);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RegistrationConflictDoesNotPreventDisposal() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        const int blockerId = 0x5651;
        bool blocked = Win32Interop.RegisterHotKey(hwnd, blockerId, 2 | 1 | 4 | 0x4000, 0x49);
        try
        {
            // The combination is occupied either by this test or another application.
            using var controller = new InvisibleHotkeyController(window, () => Assert.Fail("Unexpected hotkey dispatch"));
            Assert.False(InvisibleHotkeyController.IsRegistered);
            controller.Dispose();
            controller.Dispose();
        }
        finally
        {
            if (blocked) Win32Interop.UnregisterHotKey(hwnd, blockerId);
            window.Close();
        }
    });
}
