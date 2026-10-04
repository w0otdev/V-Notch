using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace VNotch.Tests;

internal static class BackgroundTestWindows
{
    private const int GwlExStyle = -20;
    private const int BackgroundStyles = 0x08000000 | 0x00000080 | 0x00000020; // NOACTIVATE, TOOLWINDOW, TRANSPARENT
    private const int AppWindowStyle = 0x00040000;
    private static readonly ConditionalWeakTable<Window, object> ProtectedWindows = new();
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized) return;
        if (DesktopTestMode.Enabled) { _initialized = true; return; }
        // Run before any test action constructs MainWindow. This metadata exists
        // only in the testhost process; the application assembly stays unchanged.
        RuntimeHelpers.RunClassConstructor(typeof(MainWindow).TypeHandle);
        OverrideMetadata(typeof(MainWindow));
        EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ProtectInput((Window)sender)));
        _initialized = true;
    }

    internal static void OverrideMetadata(Type windowType)
    {
        // Coercion also covers production code setting opacity or starting a
        // window fade. Child visuals and animation completion callbacks still run.
        UIElement.OpacityProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(0d, null, (_, _) => 0d));
        Window.ShowActivatedProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(false, null, (_, _) => false));
        Window.ShowInTaskbarProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(false, null, (_, _) => false));
        Window.TopmostProperty.OverrideMetadata(windowType, new FrameworkPropertyMetadata(false, null, (_, _) => false));
    }

    internal static void ProtectInput(Window window)
    {
        if (ProtectedWindows.TryGetValue(window, out _)) return;
        ProtectedWindows.Add(window, new object());
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        void AttachInputHook()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(hwnd)!;
            source.AddHook(BackgroundInputHook);
            SetWindowLong(hwnd, GwlExStyle, (GetWindowLong(hwnd, GwlExStyle) | BackgroundStyles) & ~AppWindowStyle);
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) AttachInputHook();
        else window.SourceInitialized += (_, _) => AttachInputHook();
    }

    private static IntPtr BackgroundInputHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0084) // WM_NCHITTEST: never intercept a physical desktop click.
        {
            handled = true;
            return new IntPtr(-1); // HTTRANSPARENT
        }
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        if (message == 0x007C && wParam.ToInt64() == GwlExStyle) // WM_STYLECHANGING
        {
            int style = Marshal.ReadInt32(lParam, 4); // STYLESTRUCT.styleNew
            Marshal.WriteInt32(lParam, 4, (style | BackgroundStyles) & ~AppWindowStyle);
        }
        if (message == 0x0046) // WM_WINDOWPOSCHANGING
        {
            int flagsOffset = 2 * IntPtr.Size + 4 * sizeof(int);
            Marshal.WriteInt32(lParam, flagsOffset, Marshal.ReadInt32(lParam, flagsOffset) | 0x0010); // SWP_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

internal sealed class BackgroundWindow : Window
{
    static BackgroundWindow() => BackgroundTestWindows.OverrideMetadata(typeof(BackgroundWindow));

    internal BackgroundWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        BackgroundTestWindows.ProtectInput(this);
    }
}
