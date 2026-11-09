using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VNotch.Controllers;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class LiquidGlassCapturePolicyTests
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [Theory]
    [InlineData("gpu")]
    [InlineData("cpu-excluded")]
    [InlineData("cpu-magnifier")]
    [InlineData("cpu-capture-overlay")]
    [InlineData("cpu-fallback")]
    public void ScreenshotPathsCannotClearNotchExclusion(string capturePath) => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false);
        var window = fixture.Window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var magnifier = new MagnifierCaptureSource();
        using var controller = new LiquidGlassController(new Image(), () => hwnd, () => null);
        Set(controller, "_gpuMode", capturePath == "gpu");
        controller.HideFromScreenCapture = capturePath == "cpu-excluded";
        // Exercise the common branch used by shortcut, delayed and third-party
        // capture overlays, without sending screenshot hotkeys to the user's desktop.
        Set(controller, "_nextCaptureHotkeyProbeTicks", long.MaxValue);
        Set(controller, "_lastOverlayCheckTicks", Environment.TickCount64);
        Set(controller, "_captureVisibilityUntilTicks", capturePath == "cpu-capture-overlay" ? long.MaxValue : 0L);
        if (capturePath == "cpu-magnifier")
        {
            Set(controller, "_mag", magnifier);
            Set(controller, "_magReady", true);
        }
        Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
        Set(controller, "_exactBitBltCapture", capturePath == "cpu-magnifier");

        for (int frame = 0; frame < 3; frame++)
        {
            await Task.Run(() => typeof(LiquidGlassController).GetMethod("HandleCaptureOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(controller, [16.0]));
            WindowAnimationPolicy.Attach(window);
            Assert.True(GetWindowDisplayAffinity(hwnd, out uint affinity));
            Assert.Equal(Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureExclusionFollowsTheCurrentWindowHandle(bool missingHandleDuringReplacement) => SharedStaTestRunner.RunAsync(async () =>
    {
        var first = new Window { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        var replacement = new Window { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        try
        {
            var hwnd = new WindowInteropHelper(first).EnsureHandle();
            var replacementHwnd = new WindowInteropHelper(replacement).EnsureHandle();
            WindowAnimationPolicy.Attach(first);
            WindowAnimationPolicy.Attach(replacement);
            using var controller = new LiquidGlassController(new Image(), () => hwnd, () => null);
            Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
            Assert.True(GetWindowDisplayAffinity(hwnd, out uint affinity));
            Assert.Equal(Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity);

            // A missing or replacement HWND invalidates the previous surface's cache.
            if (missingHandleDuringReplacement)
            {
                hwnd = IntPtr.Zero;
                Assert.False(await Task.Run(() => SetCaptureAffinity(controller)));
            }
            hwnd = replacementHwnd;
            Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
            Assert.True(GetWindowDisplayAffinity(hwnd, out affinity));
            Assert.Equal(Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity);
            WindowAnimationPolicy.Attach(replacement);
            Assert.True(GetWindowDisplayAffinity(hwnd, out affinity));
            Assert.Equal(Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity);
        }
        finally
        {
            first.Close();
            replacement.Close();
        }
    });

    private static bool SetCaptureAffinity(LiquidGlassController controller) =>
        (bool)typeof(LiquidGlassController).GetMethod("SetWindowDisplayAffinitySafe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [Win32Interop.WDA_EXCLUDEFROMCAPTURE])!;

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
