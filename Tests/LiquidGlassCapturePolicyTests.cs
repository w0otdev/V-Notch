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
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void GhostModeAloneControlsCaptureAcrossRenderers(bool gpu, bool ghost, bool legacyHide)
        => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false);
        var window = fixture.Window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var controller = new LiquidGlassController(new Image(), () => hwnd, () => null);
        Set(controller, "_gpuMode", gpu);
        controller.HideFromScreenCapture = legacyHide;
        try
        {
            WindowAnimationPolicy.SetInvisibleMode(ghost);
            Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
            Set(controller, "_exactBitBltCapture", true);
            for (int frame = 0; frame < 3; frame++)
            {
                Assert.False(await Task.Run(() => ApplyCapturePolicy(controller)));
                WindowAnimationPolicy.Attach(window);
                AssertAffinity(hwnd, ghost);
                Assert.Equal(ghost, Get<bool>(controller, "_exactBitBltCapture"));
            }
            // Disabling Ghost mode restores capture immediately, even when a
            // renderer previously requested exclusion and has not polled again.
            WindowAnimationPolicy.SetInvisibleMode(false);
            WindowAnimationPolicy.Attach(window);
            AssertAffinity(hwnd, false);
            await Task.Run(() => ApplyCapturePolicy(controller));
            Assert.False(Get<bool>(controller, "_exactBitBltCapture"));
        }
        finally { WindowAnimationPolicy.SetInvisibleMode(false); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MagnifierAndMissingBackdropKeepTheNotchCapturable(bool magnifierReady) => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false);
        var window = fixture.Window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        using var magnifier = new MagnifierCaptureSource();
        using var controller = new LiquidGlassController(new Image(), () => hwnd, () => null);
        Set(controller, "_mag", magnifier);
        Set(controller, "_magReady", magnifierReady);
        controller.HideFromScreenCapture = true; // Legacy settings cannot override Ghost mode.
        Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
        Set(controller, "_exactBitBltCapture", true);
        Assert.False(await Task.Run(() => ApplyCapturePolicy(controller)));
        WindowAnimationPolicy.Attach(window);
        AssertAffinity(hwnd, false);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CapturePolicyFollowsTheCurrentWindowHandle(bool missingHandleDuringReplacement, bool ghost) => SharedStaTestRunner.RunAsync(async () =>
    {
        var first = new Window { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        var replacement = new Window { ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        try
        {
            WindowAnimationPolicy.SetInvisibleMode(ghost);
            var hwnd = new WindowInteropHelper(first).EnsureHandle();
            var replacementHwnd = new WindowInteropHelper(replacement).EnsureHandle();
            WindowAnimationPolicy.Attach(first);
            WindowAnimationPolicy.Attach(replacement);
            using var controller = new LiquidGlassController(new Image(), () => hwnd, () => null);
            Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
            AssertAffinity(hwnd, ghost);
            if (missingHandleDuringReplacement)
            {
                hwnd = IntPtr.Zero;
                Assert.False(await Task.Run(() => SetCaptureAffinity(controller)));
            }
            hwnd = replacementHwnd;
            Assert.True(await Task.Run(() => SetCaptureAffinity(controller)));
            WindowAnimationPolicy.Attach(replacement);
            AssertAffinity(hwnd, ghost);
        }
        finally
        {
            WindowAnimationPolicy.SetInvisibleMode(false);
            first.Close();
            replacement.Close();
        }
    });

    private static void AssertAffinity(IntPtr hwnd, bool ghost)
    {
        Assert.True(GetWindowDisplayAffinity(hwnd, out uint affinity));
        Assert.Equal(ghost ? Win32Interop.WDA_EXCLUDEFROMCAPTURE : Win32Interop.WDA_NONE, affinity);
    }

    private static bool SetCaptureAffinity(LiquidGlassController controller) =>
        (bool)typeof(LiquidGlassController).GetMethod("SetWindowDisplayAffinitySafe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [Win32Interop.WDA_EXCLUDEFROMCAPTURE])!;
    private static bool ApplyCapturePolicy(LiquidGlassController controller) =>
        (bool)typeof(LiquidGlassController).GetMethod("ApplyCapturePolicy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [16.0])!;
    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
