using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VNotch.Services;

namespace VNotch.Controllers;

internal sealed partial class ClipboardHotkeyController : IDisposable
{
    private const int HotkeyId = 0x564F;
    private readonly Window _host;
    private readonly Action _toggle;
    private HwndSource? _source;
    private string _gesture = "";
    private bool _disposed;
    internal string? RegistrationError { get; private set; }

    internal ClipboardHotkeyController(Window host, Action toggle)
    {
        _host = host; _toggle = toggle;
        host.SourceInitialized += SourceInitialized;
        Attach();
    }
    private void SourceInitialized(object? sender, EventArgs e) { Attach(); Apply(_gesture); }
    private void Attach()
    {
        if (_disposed) return;
        IntPtr handle = new WindowInteropHelper(_host).Handle;
        if (handle == IntPtr.Zero || _source != null) return;
        _source = HwndSource.FromHwnd(handle); _source?.AddHook(WndProc);
    }
    internal void Apply(string gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gesture = gesture;
        if (_source == null) return;
        Win32Interop.UnregisterHotKey(_source.Handle, HotkeyId);
        RegistrationError = null;
        if (string.IsNullOrWhiteSpace(gesture)) return;
        if (!TryParse(gesture, out uint modifiers, out uint key)) { RegistrationError = Loc.Get("clipboard.invalidHotkey"); return; }
        vn_hotkey_is_os_reserved(modifiers, key, out byte reserved);
        if (reserved != 0)
        {
            RegistrationError = Loc.Get("clipboard.hotkeyOsReserved");
            return;
        }
        if (!Win32Interop.RegisterHotKey(_source.Handle, HotkeyId, modifiers | 0x4000, key))
            RegistrationError = Loc.Get("clipboard.hotkeyConflict");
    }
    internal static unsafe bool TryParse(string value, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        fixed (char* ptr = value)
        {
            vn_hotkey_parse_modifiers(ptr, value.Length, out uint mods, out int keyStart, out int keyLen, out byte valid);
            if (valid == 0 || keyLen <= 0) return false;
            ReadOnlySpan<char> keyToken = value.AsSpan(keyStart, keyLen);
            if (!Enum.TryParse<Key>(keyToken, true, out var parsed) || parsed == Key.None)
                return false;
            key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
            if (key == 0) return false;
            modifiers = mods;
            return true;
        }
    }
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32Interop.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
        {
            handled = true;
            _host.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _toggle(); }));
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.SourceInitialized -= SourceInitialized;
        if (_source is { IsDisposed: false }) { Win32Interop.UnregisterHotKey(_source.Handle, HotkeyId); _source.RemoveHook(WndProc); }
        _source = null;
        RegistrationError = null;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_hotkey_parse_modifiers(
        char* text, int text_len, out uint out_modifiers, out int out_key_start, out int out_key_len, out byte out_valid);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_hotkey_is_os_reserved(uint modifiers, uint vk, out byte out_reserved);
}
