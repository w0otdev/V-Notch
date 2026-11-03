using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Diagnostic only: keep the application's layered-window behavior unchanged.
internal static class DwmProbeHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { internal int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    internal static void Enable(IntPtr hwnd)
    {
        HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
    }

    internal static int Run()
    {
        var app = new Application();
        var backdrop = new Window { Title = "VNotch alpha probe backdrop", Width = 800, Height = 500,
            Left = 100, Top = 100, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = new LinearGradientBrush(Colors.Magenta, Colors.Cyan, 0), ShowInTaskbar = false };
        var content = new Border { Margin = new Thickness(48), CornerRadius = new CornerRadius(40),
            Background = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            Child = new TextBlock { Text = "DWM alpha probe\nOutside: transparent\nInside: 50% white",
                FontSize = 28, Foreground = Brushes.White, Margin = new Thickness(50) } };
        var window = new Window { Title = "VNotch DWM alpha probe", Width = 640, Height = 360,
            Left = 180, Top = 170, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Transparent, Content = content, ShowInTaskbar = true };
        Enable(new WindowInteropHelper(window).EnsureHandle());
        backdrop.Show(); window.Show();
        Console.WriteLine($"DWM alpha probe: HWND=0x{new WindowInteropHelper(window).Handle:X}, visible={IsWindowVisible(new WindowInteropHelper(window).Handle)}");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); window.Close(); backdrop.Close(); app.Shutdown(); };
        timer.Start(); app.Run();
        return 0;
    }
}
