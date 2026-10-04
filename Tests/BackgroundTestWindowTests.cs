using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Xunit;

namespace VNotch.Tests;

public sealed class BackgroundTestWindowTests
{
    [Fact]
    public void BackgroundWindowCannotBecomeVisibleOrCaptureInputWhileAnimationStillCompletes() => SharedStaTestRunner.Run(() =>
    {
        var window = new BackgroundWindow
        {
            Width = 100,
            Height = 100,
            Left = -10000,
            Top = -10000,
            Content = new Border(),
            Opacity = 1,
            ShowActivated = true,
            ShowInTaskbar = true,
            Topmost = true
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(window.IsVisible); // WPF layout and animation are exercised.
            Assert.Equal(0, window.Opacity);
            Assert.False(window.ShowActivated);
            Assert.False(window.ShowInTaskbar);
            Assert.False(window.Topmost);
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            Assert.NotEqual(hwnd, GetForegroundWindow());
            Assert.Equal(new IntPtr(-1), SendMessage(hwnd, 0x0084, IntPtr.Zero, IntPtr.Zero)); // hit-test
            Assert.Equal(new IntPtr(3), SendMessage(hwnd, 0x0021, IntPtr.Zero, IntPtr.Zero)); // no activation

            bool completed = false;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80));
            animation.Completed += (_, _) => completed = true;
            window.BeginAnimation(UIElement.OpacityProperty, animation);
            PumpUntil(() => completed);
            Assert.Equal(0, window.Opacity);
            Assert.NotEqual(hwnd, GetForegroundWindow());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MainWindowOpacitySettingsHonorTheSelectedTestMode() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = new GreetingAcceptanceTests.MainWindowFixture("en", greeting: false);
        var window = Assert.IsType<MainWindow>(fixture.Window);
        window.Opacity = 0.9; // The same property set by ApplySettings.
        window.ShowActivated = true;
        window.ShowInTaskbar = true;
        window.Topmost = true;
        Assert.Equal(DesktopTestMode.Enabled ? 0.9 : 0, window.Opacity);
        Assert.Equal(DesktopTestMode.Enabled, window.ShowActivated);
        Assert.Equal(DesktopTestMode.Enabled, window.ShowInTaskbar);
        Assert.Equal(DesktopTestMode.Enabled, window.Topmost);
    });

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        long deadline = Environment.TickCount64 + 3000;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || Environment.TickCount64 >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "Background window animation did not complete.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
