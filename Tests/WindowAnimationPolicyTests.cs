using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WindowAnimationPolicyTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [Fact]
    public void InvisibleModeExcludesCaptureAndRestoresTaskbarAndRequestedAffinity() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowInTaskbar = true, ShowActivated = false, Opacity = 0 };
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            WindowAnimationPolicy.Attach(window);
            WindowAnimationPolicy.SetInvisibleMode(true);
            WindowAnimationPolicy.Attach(window);
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert.False(window.ShowInTaskbar);
            Assert.NotEqual(0, Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE) & Win32Interop.WS_EX_TOOLWINDOW);
            // Glass cleanup cannot remove the user's capture exclusion.
            Assert.True(Win32Interop.SetWindowDisplayAffinity(hwnd, Win32Interop.WDA_NONE));
            Assert.True(GetWindowDisplayAffinity(hwnd, out uint affinity));
            Assert.Equal(Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity);
            WindowAnimationPolicy.SetInvisibleMode(false);
            WindowAnimationPolicy.Attach(window);
            Assert.True(window.ShowInTaskbar);
            hwnd = new WindowInteropHelper(window).Handle;
            Assert.True(GetWindowDisplayAffinity(hwnd, out affinity));
            Assert.Equal(Win32Interop.WDA_NONE, affinity);
        }
        finally
        {
            WindowAnimationPolicy.SetInvisibleMode(false);
            window.Close();
        }
    });

    [Fact]
    public void FutureAuxiliaryWindowsAutomaticallyUseToolWindowStylesAndKeepWpfAnimation() => SharedStaTestRunner.RunAsync(async ct =>
    {
        WindowAnimationPolicy.Initialize();
        WindowAnimationPolicy.Initialize();
        // No explicit Attach call: this is a future window using the app-wide policy.
        var window = new Window { Width = 120, Height = 80, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var hwnd = new WindowInteropHelper(window).Handle;
            int style = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Assert.NotEqual(0, style & Win32Interop.WS_EX_TOOLWINDOW);
            Assert.Equal(0, style & Win32Interop.WS_EX_APPWINDOW);
            var animated = new System.Windows.Controls.Border { Opacity = 0 };
            window.Content = animated;
            animated.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
            await WpfFrameWaiter.UntilAsync(() => animated.Opacity >= .99, "native policy preserves WPF visual animation", ct);
            window.Hide();
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.NotEqual(0, Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE) & Win32Interop.WS_EX_TOOLWINDOW);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NativePolicyDoesNotHideTaskbarWindowsOrOverrideCancelledClosing() => SharedStaTestRunner.Run(() =>
    {
        var window = new Window { ShowInTaskbar = true, ShowActivated = false, Opacity = 0 };
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int originalStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            WindowAnimationPolicy.Attach(window);
            WindowAnimationPolicy.Attach(window);
            Assert.Equal(originalStyle, Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE));
            bool cancel = true;
            int closing = 0;
            window.Closing += (_, e) => { closing++; e.Cancel = cancel; };
            window.Close();
            Assert.Equal(1, closing);
            Assert.Equal(hwnd, new WindowInteropHelper(window).Handle);
            cancel = false;
            window.Close();
            Assert.Equal(2, closing);
        }
        finally
        {
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) window.Close();
        }
    });
}
