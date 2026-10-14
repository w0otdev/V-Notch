using System.Windows;
using System.Windows.Interop;
using VNotch.Services;

namespace VNotch.Controllers;

internal sealed class InvisibleHotkeyController : IDisposable
{
    internal const int HotkeyId = 0x5650;
    private readonly Window _host;
    private readonly Action _toggle;
    private HwndSource? _source;
    internal static bool IsRegistered { get; private set; }

    internal InvisibleHotkeyController(Window host, Action toggle)
    {
        _host = host;
        _toggle = toggle;
        host.SourceInitialized += OnSourceInitialized;
        Attach();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Attach();

    private void Attach()
    {
        var hwnd = new WindowInteropHelper(_host).Handle;
        if (hwnd == IntPtr.Zero || _source != null) return;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source == null) return;
        _source.AddHook(WndProc);
        // Ctrl + Alt + Shift + I; MOD_NOREPEAT prevents held keys toggling repeatedly.
        IsRegistered = Win32Interop.RegisterHotKey(hwnd, HotkeyId, 2 | 1 | 4 | 0x4000, 0x49);
        if (!IsRegistered) RuntimeLog.Log("WINDOW-PRIVACY", "Invisible shortcut unavailable; open settings from the notch.");
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32Interop.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
        {
            handled = true;
            _host.Dispatcher.BeginInvoke(new Action(() => { if (_source != null) _toggle(); }));
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _host.SourceInitialized -= OnSourceInitialized;
        if (_source is { IsDisposed: false })
        {
            if (IsRegistered) Win32Interop.UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
        }
        _source = null;
        IsRegistered = false;
    }
}
