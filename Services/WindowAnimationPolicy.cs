using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<IntPtr, WindowLifetime> WindowHandles = new();
    private static int _initialized;
    internal static bool InvisibleMode { get; private set; }
    internal static bool ShadowEnabled { get; private set; } = true;

    private static readonly DependencyProperty OriginalShadowOpacityProperty =
        DependencyProperty.RegisterAttached(
            "OriginalShadowOpacity",
            typeof(double),
            typeof(WindowAnimationPolicy),
            new PropertyMetadata(-1.0));

    internal static void SetShadowEnabled(bool enabled)
    {
        ShadowEnabled = enabled;
        if (Application.Current == null) return;
        Application.Current.Dispatcher.VerifyAccess();
        foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) Attach(window);
    }

    internal static void ApplyWindowShadowPolicy(Window window, bool enabled)
    {
        switch (window)
        {
            case MainWindow mainWindow:
                mainWindow.ApplyWindowShadow(enabled);
                return;
            case SettingsWindow settingsWindow:
                settingsWindow.ApplyWindowShadow(enabled);
                return;
            case SpotlightWindow spotlightWindow:
                spotlightWindow.ApplyWindowShadow(enabled);
                return;
            case TranslationWindow translationWindow:
                translationWindow.ApplyWindowShadow(enabled);
                return;
        }

        if (window.Content is DependencyObject contentRoot)
        {
            ApplyDropShadowTree(contentRoot, enabled);
        }
    }

    private static void ApplyDropShadowTree(DependencyObject node, bool enabled)
    {
        if (node is UIElement element && element.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            if (!enabled)
            {
                if ((double)element.GetValue(OriginalShadowOpacityProperty) < 0)
                {
                    element.SetValue(OriginalShadowOpacityProperty, dse.Opacity);
                }
                dse.Opacity = 0.0;
            }
            else
            {
                double orig = (double)element.GetValue(OriginalShadowOpacityProperty);
                if (orig >= 0)
                {
                    dse.Opacity = orig;
                }
            }
        }

        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject depChild)
            {
                ApplyDropShadowTree(depChild, enabled);
            }
        }
    }

    internal static void SetInvisibleMode(bool enabled)
    {
        InvisibleMode = enabled;
        if (Application.Current == null) return;
        Application.Current.Dispatcher.VerifyAccess();
        foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) Attach(window);
    }

    internal static bool SetCaptureAffinity(IntPtr hwnd, uint affinity)
    {
        // HwndSource lookup is dispatcher-local. Capture workers must use the
        // registered lifetime so a later UI refresh retains their request.
        if (WindowHandles.TryGetValue(hwnd, out var lifetime))
            return lifetime.SetCaptureAffinity(hwnd, affinity);
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is Window window)
            return Windows.GetValue(window, static key => new WindowLifetime(key)).SetCaptureAffinity(hwnd, affinity);
        return Win32Interop.SetWindowDisplayAffinityNative(hwnd,
            InvisibleMode ? Win32Interop.WDA_EXCLUDEFROMCAPTURE : Win32Interop.WDA_NONE);
    }

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
        private readonly object _captureSync = new();
        private HwndSource? _source;
        private IntPtr _hwnd;
        private bool _invisibleApplied;
        private bool _originalTaskbar;
        private int _originalShellStyle;

        internal WindowLifetime(Window window)
        {
            _window = window;
            window.SourceInitialized += OnSourceInitialized;
            window.IsVisibleChanged += OnVisibilityChanged;
            window.Closing += OnClosing;
            window.Closed += OnClosed;
            TrackHandle(new WindowInteropHelper(window).Handle);
        }

        internal bool SetCaptureAffinity(IntPtr hwnd, uint affinity)
        {
            lock (_captureSync)
            {
                return Win32Interop.SetWindowDisplayAffinityNative(hwnd,
                    EffectiveCaptureAffinity);
            }
        }

        // Ghost (Invisible) mode is the only reason to exclude an app window.
        // Renderer/backend preferences must never hide it from ordinary captures.
        private uint EffectiveCaptureAffinity => InvisibleMode
            ? Win32Interop.WDA_EXCLUDEFROMCAPTURE : Win32Interop.WDA_NONE;

        private void TrackHandle(IntPtr hwnd)
        {
            if (_hwnd == hwnd) return;
            if (_hwnd != IntPtr.Zero) WindowHandles.TryRemove(_hwnd, out _);
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            _hwnd = hwnd;
            _source = hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(hwnd);
            if (hwnd == IntPtr.Zero) return;
            WindowHandles[hwnd] = this;
            _source?.AddHook(WindowProc);
        }

        internal void Apply()
        {
            ApplyWindowShadowPolicy(_window, ShadowEnabled);
            if (_invisibleApplied != InvisibleMode)
            {
                _invisibleApplied = InvisibleMode;
                if (InvisibleMode)
                {
                    _originalTaskbar = _window.ShowInTaskbar;
                    var originalHandle = new WindowInteropHelper(_window).Handle;
                    _originalShellStyle = originalHandle == IntPtr.Zero ? 0 :
                        Win32Interop.GetWindowLong(originalHandle, Win32Interop.GWL_EXSTYLE) &
                        (Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_APPWINDOW);
                    _window.ShowInTaskbar = false;
                }
                else
                {
                    _window.ShowInTaskbar = _originalTaskbar;
                    var restoredHandle = new WindowInteropHelper(_window).Handle;
                    if (restoredHandle != IntPtr.Zero)
                    {
                        int style = Win32Interop.GetWindowLong(restoredHandle, Win32Interop.GWL_EXSTYLE);
                        UpdateShellStyle(restoredHandle,
                            (style & ~(Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_APPWINDOW)) | _originalShellStyle);
                    }
                }
            }
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) return;
            TrackHandle(hwnd);
            lock (_captureSync)
            {
                if (!Win32Interop.SetWindowDisplayAffinityNative(hwnd,
                    EffectiveCaptureAffinity))
                    RuntimeLog.Log("WINDOW-PRIVACY", $"Capture exclusion failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }

            int disabled = 1; // Win32 BOOL, not a one-byte managed bool.
            _ = Win32Interop.DwmSetWindowAttributeInt(hwnd,
                Win32Interop.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));

            if (!_window.ShowInTaskbar)
            {
                int style = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
                int desired = (style | Win32Interop.WS_EX_TOOLWINDOW) & ~Win32Interop.WS_EX_APPWINDOW;
                if (desired != style)
                    UpdateShellStyle(hwnd, desired);
            }
        }

        private static void UpdateShellStyle(IntPtr hwnd, int style)
        {
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, style);
            Win32Interop.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE |
                Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOOWNERZORDER);
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
            TrackHandle(IntPtr.Zero);
            _window.SourceInitialized -= OnSourceInitialized;
            _window.IsVisibleChanged -= OnVisibilityChanged;
            _window.Closing -= OnClosing;
            _window.Closed -= OnClosed;
            Windows.Remove(_window);
        }
    }
}
