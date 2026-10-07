using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using VNotch.Controllers;
using VNotch.Services;
using Xunit;
using static VNotch.Services.Win32Interop;

namespace VNotch.Tests;

public class OverlayWindowControllerTests
{
    [Fact]
    public void PopupStaysAboveTheOverlayThroughActivationAndResize() => SharedStaTestRunner.Run(() =>
    {
        var window = new BackgroundWindow { Width = 40, Height = 40, Left = -32000, Top = -32000 };
        var popup = new BackgroundWindow { Width = 40, Height = 40, Left = -32000, Top = -32000 };
        var state = new NotchShellState { FixedX = -32000, FixedY = -32000, WindowWidth = 40, WindowHeight = 40 };
        bool popupOpen = false;
        using var controller = new OverlayWindowController(window, state, () => true, () => false,
            () => { }, () => { }, () => { }, () => { })
        { IsZOrderSuspended = () => popupOpen };
        try
        {
            window.Show(); popup.Show();
            controller.Initialize();
            state.HasFixedBounds = true;
            IntPtr popupHandle = new WindowInteropHelper(popup).Handle;
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            Assert.True(SetWindowPos(state.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags));
            popupOpen = true;
            Assert.True(SetWindowPos(popupHandle, HWND_TOPMOST, 0, 0, 0, 0, flags));
            Assert.True(IsWindowAbove(popupHandle, state.Hwnd));

            NotifyActivation(controller, state.Hwnd);
            controller.ReassertBounds();
            Assert.True(IsWindowAbove(popupHandle, state.Hwnd));

            popupOpen = false;
            NotifyActivation(controller, state.Hwnd);
            Assert.True(IsWindowAbove(state.Hwnd, popupHandle));
        }
        finally { controller.Dispose(); popup.Close(); window.Close(); }
    });

    private static void NotifyActivation(OverlayWindowController controller, IntPtr handle)
    {
        object[] arguments = [handle, WM_ACTIVATE, new IntPtr(1), IntPtr.Zero, false];
        typeof(OverlayWindowController).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, arguments);
    }

    private static bool IsWindowAbove(IntPtr above, IntPtr below)
    {
        for (IntPtr current = GetWindow(below, GW_HWNDPREV); current != IntPtr.Zero; current = GetWindow(current, GW_HWNDPREV))
            if (current == above) return true;
        return false;
    }

    [Fact]
    public void OpenPopupPreservesZOrderWhileKeepingTheNotchAtItsFixedBounds() => SharedStaTestRunner.Run(() =>
    {
        var window = new BackgroundWindow();
        var state = new NotchShellState { FixedX = 100, FixedY = 0, WindowWidth = 800, WindowHeight = 360 };
        bool popupOpen = true;
        using var controller = new OverlayWindowController(window, state, () => true, () => false,
            () => { }, () => { }, () => { }, () => { })
        { IsZOrderSuspended = () => popupOpen };
        var requested = new WINDOWPOS { hwndInsertAfter = new IntPtr(123), x = 10, y = 20, cx = 50, cy = 60 };

        var suspended = ApplyWindowPosition(controller, requested);
        Assert.NotEqual(0u, suspended.flags & SWP_NOZORDER);
        Assert.Equal(requested.hwndInsertAfter, suspended.hwndInsertAfter);
        Assert.Equal((100, 0, 800, 360), (suspended.x, suspended.y, suspended.cx, suspended.cy));

        popupOpen = false;
        var resumed = ApplyWindowPosition(controller, requested);
        Assert.Equal(0u, resumed.flags & SWP_NOZORDER);
        Assert.Equal(HWND_TOPMOST, resumed.hwndInsertAfter);
    });

    [Fact]
    public void BoundsOnlyUpdatesDoNotRaiseTheNotch() => SharedStaTestRunner.Run(() =>
    {
        var window = new BackgroundWindow();
        var state = new NotchShellState { FixedX = 100, WindowWidth = 800, WindowHeight = 360 };
        using var controller = new OverlayWindowController(window, state, () => true, () => false,
            () => { }, () => { }, () => { }, () => { });
        var requested = new WINDOWPOS { hwndInsertAfter = new IntPtr(123), flags = SWP_NOZORDER };
        var applied = ApplyWindowPosition(controller, requested);
        Assert.Equal(requested.hwndInsertAfter, applied.hwndInsertAfter);
        Assert.NotEqual(0u, applied.flags & SWP_NOZORDER);
        Assert.Equal((100, 0, 800, 360), (applied.x, applied.y, applied.cx, applied.cy));
    });

    private static WINDOWPOS ApplyWindowPosition(OverlayWindowController controller, WINDOWPOS requested)
    {
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINDOWPOS>());
        try
        {
            Marshal.StructureToPtr(requested, pointer, false);
            typeof(OverlayWindowController).GetMethod("HandleWindowPosChanging", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(controller, [pointer]);
            return Marshal.PtrToStructure<WINDOWPOS>(pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public void CalculateCenteredBounds_UsesPhysicalPixelsAndScreenOffset()
    {
        var bounds = OverlayWindowController.CalculateCenteredBounds(
            screenLeft: -1920, screenWidth: 1920, widthDip: 500, heightDip: 200, dpiScale: 1.5);

        Assert.Equal(-1335, bounds.X);
        Assert.Equal(750, bounds.Width);
        Assert.Equal(300, bounds.Height);
    }

    [Fact]
    public void CalculateCenteredBounds_CentersTheStartupHostAt150PercentScale()
    {
        var bounds = OverlayWindowController.CalculateCenteredBounds(
            screenLeft: 0, screenWidth: 2560, widthDip: 816, heightDip: 200, dpiScale: 1.5);

        Assert.Equal(668, bounds.X);
        Assert.Equal(1224, bounds.Width);
        Assert.Equal(300, bounds.Height);
    }

    [Fact]
    public void TryApplyDesktopLayerZOrder_RefreshesExplorerAnchorAfterFailure()
    {
        var staleAnchor = new IntPtr(1);
        var refreshedAnchor = new IntPtr(2);
        var attemptedAnchors = new List<IntPtr>();
        int resolveCount = 0;

        bool applied = OverlayWindowController.TryApplyDesktopLayerZOrder(
            getDesktopAnchor: () => ++resolveCount == 1 ? staleAnchor : refreshedAnchor,
            applyZOrder: anchor =>
            {
                attemptedAnchors.Add(anchor);
                return anchor == refreshedAnchor;
            });

        Assert.True(applied);
        Assert.Equal(new[] { staleAnchor, refreshedAnchor }, attemptedAnchors);
        Assert.Equal(2, resolveCount);
    }
}
