using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ScreenCaptureActivityTests
{
    [Theory]
    [InlineData("ScreenClippingHost", "", false, true)]
    [InlineData("Microsoft.ScreenClipping.Overlay", "", false, true)]
    [InlineData("Windows.UI.Core.CoreWindow", "ScreenClippingHost", false, true)]
    [InlineData("WinUIDesktopWin32WindowClass", "SnippingTool", true, true)]
    [InlineData("WinUIDesktopWin32WindowClass", "SnippingTool", false, false)]
    [InlineData("WindowsForms10.Window", "ShareX", true, true)]
    [InlineData("WindowsForms10.Window", "ShareX", false, false)]
    [InlineData("GameWindow", "game", true, false)]
    [InlineData("", "", false, false)]
    public void CaptureOverlaysAreDistinguishedFromEditorsAndNormalFullscreenApps(
        string className, string process, bool fullscreen, bool expected) =>
        Assert.Equal(expected, ScreenCaptureActivity.IsCaptureOverlay(className, process, fullscreen));

    [Fact]
    public void ProtectionLastsThroughOverlayInputHandoffThenExpires()
    {
        long now = 100;
        IntPtr foreground = new(1);
        var activity = new ScreenCaptureActivity(() => foreground, hwnd => hwnd == new IntPtr(1), () => false, () => now, () => IntPtr.Zero);
        Assert.True(activity.IsActive());
        foreground = new(2);
        now = 1099;
        Assert.True(activity.IsActive());
        now = 1100;
        Assert.False(activity.IsActive());
        now = 1500;
        Assert.False(activity.IsActive()); // Ordinary foreground windows cannot extend protection.
    }

    [Fact]
    public void CaptureShortcutProtectsBeforeTheOverlayTakesFocus()
    {
        long now = 100;
        bool keyDown = false;
        var activity = new ScreenCaptureActivity(() => IntPtr.Zero, _ => false, () => keyDown, () => now, () => IntPtr.Zero);
        Assert.False(activity.IsActive());
        keyDown = true;
        Assert.True(activity.IsActive());
        keyDown = false;
        now = 900;
        Assert.True(activity.IsActive());
        now = 1100;
        Assert.False(activity.IsActive());
    }

    [Fact]
    public void ForegroundNotificationProtectsEvenIfFocusChangesBeforeTheUiCallbackRuns()
    {
        var activity = new ScreenCaptureActivity(() => new IntPtr(2), hwnd => hwnd == new IntPtr(1), () => false, () => 100, () => IntPtr.Zero);
        Assert.True(activity.IsActive(new IntPtr(1)));
        Assert.True(activity.IsActive());
    }

    [Fact]
    public void NonActivatingCaptureOverlayUnderThePointerKeepsProtectionActive()
    {
        long now = 100;
        IntPtr pointerWindow = new(1);
        var activity = new ScreenCaptureActivity(() => new IntPtr(2), hwnd => hwnd == new IntPtr(1), () => false, () => now, () => pointerWindow);
        Assert.True(activity.IsActive());
        now = 10_000;
        Assert.True(activity.IsActive());
        pointerWindow = new(2);
        now = 11_000;
        Assert.False(activity.IsActive());
    }
}
