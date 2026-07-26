using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public interface ISpotlightController : IDisposable
{
    bool IsHotkeyRegistered { get; }
    void Initialize(Window host, NotchSettings settings);
    void ApplySettings(NotchSettings settings);
}

internal sealed class SpotlightController : ISpotlightController
{
    private const int HotkeyId = 0x564E;
    private const uint EscapeVirtualKey = 0x1B;
    private const uint StaleFallbackKeyDownMs = 500;
    private readonly Func<SpotlightWindow> _windowFactory;
    private SpotlightWindow? _window;
    private Window? _host;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _keyboardHook;
    private LowLevelKeyboardProc? _keyboardProc;
    private bool _nativeRegistered;
    private bool _fallbackSpaceDown;
    private bool _escapeDown;
    private uint _lastFallbackSpaceEventTime;
    private bool _disposed;

    public bool IsHotkeyRegistered => _nativeRegistered || _keyboardHook != IntPtr.Zero;

    public SpotlightController(Func<SpotlightWindow> windowFactory)
    {
        _windowFactory = windowFactory;
    }

    public void Initialize(Window host, NotchSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source != null) return;

        _host = host;
        _hwnd = new WindowInteropHelper(host).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        ApplySettings(settings);
    }

    public void ApplySettings(NotchSettings settings)
    {
        if (_hwnd == IntPtr.Zero) return;
        DisableHotkey();

        if (!settings.EnableSpotlight)
        {
            _window?.HideSpotlight();
            return;
        }

        _nativeRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_SPACE);
        if (_nativeRegistered)
        {
            RuntimeLog.Log("SPOTLIGHT-HOTKEY", "Alt+Space registered with Windows");
            if (!EnsureKeyboardHook())
                RuntimeLog.Warn("SPOTLIGHT-HOTKEY", "Global Escape shortcut is unavailable");
            return;
        }

        int error = Marshal.GetLastWin32Error();
        if (error == 1409 && EnsureKeyboardHook())
        {
            RuntimeLog.Warn("SPOTLIGHT-HOTKEY",
                "Alt+Space is owned by another app; keyboard fallback enabled");
            return;
        }

        RuntimeLog.Warn("SPOTLIGHT-HOTKEY",
            $"Could not enable Alt+Space (Win32={error})");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleSpotlight();
            handled = true;
        }
        return IntPtr.Zero;
    }

    internal static bool IsAltSpaceKey(uint vkCode, uint flags) =>
        vkCode == VK_SPACE && (flags & LLKHF_ALTDOWN) != 0;

    internal static bool IsEscapeKey(uint vkCode) => vkCode == EscapeVirtualKey;

    internal static bool ShouldDispatchFallbackToggle(
        bool spaceIsAlreadyDown,
        uint lastSpaceEventTime,
        uint currentTime) =>
        !spaceIsAlreadyDown || unchecked(currentTime - lastSpaceEventTime) > StaleFallbackKeyDownMs;

    private bool EnsureKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return true;
        _keyboardProc = KeyboardHookProc;
        string? moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
        _keyboardHook = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _keyboardProc,
            GetModuleHandle(moduleName),
            0);
        if (_keyboardHook != IntPtr.Zero) return true;

        _keyboardProc = null;
        RuntimeLog.Warn("SPOTLIGHT-HOTKEY",
            $"Could not install keyboard hook (Win32={Marshal.GetLastWin32Error()})");
        return false;
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int message = wParam.ToInt32();

            if (_escapeDown
                && IsEscapeKey(key.vkCode)
                && message is WM_KEYUP or WM_SYSKEYUP)
            {
                _escapeDown = false;
                return new IntPtr(1);
            }

            if (IsEscapeKey(key.vkCode)
                && _window?.IsVisible == true
                && message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (!_escapeDown)
                {
                    _escapeDown = true;
                    _source?.Dispatcher.BeginInvoke(_window.HandleGlobalEscape);
                }
                return new IntPtr(1);
            }

            if (_fallbackSpaceDown
                && key.vkCode == VK_SPACE
                && message is WM_KEYUP or WM_SYSKEYUP)
            {
                _fallbackSpaceDown = false;
                return new IntPtr(1);
            }

            if (!_nativeRegistered && IsAltSpaceKey(key.vkCode, key.flags))
            {
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (ShouldDispatchFallbackToggle(
                            _fallbackSpaceDown,
                            _lastFallbackSpaceEventTime,
                            key.time))
                    {
                        _fallbackSpaceDown = true;
                        _source?.Dispatcher.BeginInvoke(ToggleSpotlight);
                    }
                    _lastFallbackSpaceEventTime = key.time;
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void ToggleSpotlight()
    {
        if (_disposed) return;
        if (_window == null)
        {
            _window = _windowFactory();
            if (_host != null) _window.Owner = _host;
        }
        _window.ToggleFromHotkey();
    }

    private void DisableHotkey()
    {
        if (_nativeRegistered) UnregisterHotKey(_hwnd, HotkeyId);
        _nativeRegistered = false;
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _keyboardProc = null;
        _fallbackSpaceDown = false;
        _escapeDown = false;
        _lastFallbackSpaceEventTime = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisableHotkey();
        _source?.RemoveHook(WndProc);
        _source = null;
        _host = null;
        _window?.Shutdown();
        _window = null;
    }
}
