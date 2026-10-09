using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace VNotch.Services;

/// <summary>
/// App-wide native window policy. WPF owns our visual transitions; auxiliary
/// windows must not be advertised to desktop docks as normal application windows.
/// Third-party animation engines may additionally require their own exclusions.
/// </summary>
internal static class WindowAnimationPolicy
{
    private static readonly ConditionalWeakTable<Window, WindowLifetime> Windows = new();
    private static int _initialized;

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        // A class handler also covers future Window subclasses without requiring
        // every dialog or popup to remember a constructor/closing call.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach((Window)sender)));
    }

    internal static void Attach(Window window)
    {
        window.Dispatcher.VerifyAccess();
        Windows.GetValue(window, static key => new WindowLifetime(key)).Apply();
    }

    private sealed class WindowLifetime
    {
        private readonly Window _window;
        private HwndSource? _source;

        internal WindowLifetime(Window window)
        {
            _window = window;
            window.SourceInitialized += OnSourceInitialized;
            window.IsVisibleChanged += OnVisibilityChanged;
            window.Closing += OnClosing;
            window.Closed += OnClosed;
        }

        internal void Apply()
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (_source == null)
            {
                _source = HwndSource.FromHwnd(hwnd);
                _source?.AddHook(WindowProc);
            }

            int disabled = 1; // Win32 BOOL, not a one-byte managed bool.
            _ = Win32Interop.DwmSetWindowAttributeInt(hwnd,
                Win32Interop.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));

            if (!_window.ShowInTaskbar)
            {
                int style = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
                int desired = (style | Win32Interop.WS_EX_TOOLWINDOW) & ~Win32Interop.WS_EX_APPWINDOW;
                if (desired != style)
                    Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, desired);
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e) => Apply();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_window.IsVisible) Apply();
        }
        private void OnClosing(object? sender, CancelEventArgs e) => Apply();

        private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x031E) Apply(); // WM_DWMCOMPOSITIONCHANGED
            return IntPtr.Zero;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            _source = null;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.IsVisibleChanged -= OnVisibilityChanged;
            _window.Closing -= OnClosing;
            _window.Closed -= OnClosed;
            Windows.Remove(_window);
        }
    }
}
