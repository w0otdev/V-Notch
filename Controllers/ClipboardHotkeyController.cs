using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VNotch.Services;

namespace VNotch.Controllers;

internal sealed class ClipboardHotkeyController : IDisposable
{
    private const int HotkeyId = 0x564F;
    private readonly Window _host;
    private readonly Action _toggle;
    private HwndSource? _source;
    private string _gesture = "";
    internal static string? RegistrationError { get; private set; }

    /// <summary>
    /// Hotkey combinations permanently reserved by Windows shell that RegisterHotKey will silently
    /// fail on. Format: (modifiers_flags, vk). MOD_WIN=8, MOD_SHIFT=4, MOD_CTRL=2, MOD_ALT=1.
    /// Checking these before calling the API lets us show a meaningful message to the user.
    /// </summary>
    private static readonly HashSet<(uint Mods, uint Vk)> OsReservedHotkeys =
    [
        // Win+V — Windows Clipboard History (Windows 10 1809+)
        (8, 0x56),
        // Win+Shift+S — Snipping Tool
        (8 | 4, 0x53),
        // Win+Tab — Task View
        (8, 0x09),
        // Win+PrintScreen — Screenshot to Pictures
        (8, 0x2C),
    ];
    internal ClipboardHotkeyController(Window host, Action toggle)
    {
        _host = host; _toggle = toggle;
        host.SourceInitialized += SourceInitialized;
        Attach();
    }
    private void SourceInitialized(object? sender, EventArgs e) { Attach(); Apply(_gesture); }
    private void Attach()
    {
        IntPtr handle = new WindowInteropHelper(_host).Handle;
        if (handle == IntPtr.Zero || _source != null) return;
        _source = HwndSource.FromHwnd(handle); _source?.AddHook(WndProc);
    }
    internal void Apply(string gesture)
    {
        _gesture = gesture;
        if (_source == null) return;
        Win32Interop.UnregisterHotKey(_source.Handle, HotkeyId);
        RegistrationError = null;
        if (string.IsNullOrWhiteSpace(gesture)) return;
        if (!TryParse(gesture, out uint modifiers, out uint key)) { RegistrationError = Loc.Get("clipboard.invalidHotkey"); return; }
        // Guard against OS-reserved hotkeys before RegisterHotKey silently returns false.
        // The 0x4000 (MOD_NOREPEAT) flag is not part of the reserved-key identity, strip it for lookup.
        if (OsReservedHotkeys.Contains((modifiers, key)))
        {
            RegistrationError = Loc.Get("clipboard.hotkeyOsReserved");
            return;
        }
        if (!Win32Interop.RegisterHotKey(_source.Handle, HotkeyId, modifiers | 0x4000, key))
            RegistrationError = Loc.Get("clipboard.hotkeyConflict");
    }
    internal static bool TryParse(string value, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        foreach (string part in parts[..^1])
        {
            uint flag = part.ToLowerInvariant() switch { "ctrl" or "control" => 2, "shift" => 4, "alt" => 1, "win" => 8, _ => 0 };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        try
        {
            var parsed = (Key)new KeyConverter().ConvertFromString(parts[^1])!;
            key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
            return key != 0 && (modifiers & (1 | 2 | 8)) != 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException) { return false; }
    }
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32Interop.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        { handled = true; _host.Dispatcher.BeginInvoke(_toggle); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        _host.SourceInitialized -= SourceInitialized;
        if (_source != null) { Win32Interop.UnregisterHotKey(_source.Handle, HotkeyId); _source.RemoveHook(WndProc); _source = null; }
    }
}
