using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.TestSupport;
using Xunit;

namespace VNotch.Tests;

public sealed class BackgroundTestWindowTests
{
    [Fact]
    public void TestWindowsAndModalDialogsStayOutsideTheUsersInputDesktop() => SharedStaTestRunner.Run(() =>
    {
        IntPtr inputDesktop = OpenInputDesktop(0, false, 0x0041); // READOBJECTS, ENUMERATE
        Assert.NotEqual(IntPtr.Zero, inputDesktop);
        var window = new Window
        {
            Width = 120,
            Height = 80,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Opacity = 1,
            ShowActivated = true,
            ShowInTaskbar = true,
            Topmost = true
        };
        try
        {
            Assert.Equal(BackgroundTestDesktop.Name, DesktopName(GetThreadDesktop(GetCurrentThreadId())));
            Assert.NotEqual(BackgroundTestDesktop.Name, DesktopName(inputDesktop));
            window.Show();
            window.Activate();
            AssertNotOnInputDesktop(window, inputDesktop);

            var dialog = new Window
            {
                Owner = window,
                Width = 100,
                Height = 60,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Opacity = 1,
                ShowActivated = true,
                Topmost = true
            };
            Exception? modalFailure = null;
            dialog.Loaded += (_, _) =>
            {
                try { AssertNotOnInputDesktop(dialog, inputDesktop); }
                catch (Exception ex) { modalFailure = ex; }
                finally { dialog.DialogResult = false; }
            };
            Assert.False(dialog.ShowDialog());
            if (modalFailure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(modalFailure).Throw();
        }
        finally { window.Close(); CloseDesktop(inputDesktop); }
    });

    [Fact]
    public void VisibleDesktopOptInCannotBypassBackgroundModeInAnInteractiveSession()
    {
        if (!Environment.UserInteractive) return;
        string? previous = Environment.GetEnvironmentVariable(DesktopTestMode.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesktopTestMode.EnvironmentVariable, "1");
            Assert.False(DesktopTestMode.Enabled);
            Assert.NotNull(new DesktopFactAttribute().Skip);
            Assert.NotNull(new DesktopTheoryAttribute().Skip);
        }
        finally { Environment.SetEnvironmentVariable(DesktopTestMode.EnvironmentVariable, previous); }
    }

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

    private static string DesktopName(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        Assert.True(GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _));
        return name.ToString();
    }

    private static void AssertNotOnInputDesktop(Window window, IntPtr inputDesktop)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, hwnd);
        bool found = false;
        Assert.True(EnumDesktopWindows(inputDesktop, (candidate, _) => { found |= candidate == hwnd; return true; }, IntPtr.Zero));
        Assert.False(found, "A test window escaped onto the user's input desktop.");
    }

    private delegate bool EnumDesktopWindowCallback(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder information, int length, out int needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumDesktopWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
