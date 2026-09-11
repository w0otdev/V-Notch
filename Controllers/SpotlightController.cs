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
    private const string LogTag = "SPOTLIGHT-HOTKEY";
    private const int HotkeyId = 0x564E;
    private const uint EscapeVirtualKey = 0x1B;
    private const uint StaleFallbackKeyDownMs = 500;
    private readonly Func<SpotlightWindow> _windowFactory;
    private SpotlightWindow? _window;
    private Window? _host;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _keyboardHook;
#pragma warning disable IDE0052 // Keep delegate reference alive to prevent GC collection during native low-level keyboard hook
    private LowLevelKeyboardProc? _keyboardProc;
#pragma warning restore IDE0052
    private bool _nativeRegistered;
    private bool _fallbackSpaceDown;
    private bool _escapeDown;
    private uint _lastFallbackSpaceEventTime;
    private NotchSettings? _settings;
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
        _hwnd = new WindowInteropHelper(host).EnsureHandle();
        if (_hwnd != IntPtr.Zero)
        {
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }
        ApplySettings(settings);
    }

    public void ApplySettings(NotchSettings settings)
    {
        bool spotlightChanged = _settings == null || _settings.EnableSpotlight != settings.EnableSpotlight;
        bool glassChanged = _settings == null
            || !string.Equals(_settings.NotchStyle, settings.NotchStyle, StringComparison.OrdinalIgnoreCase)
            || !(_settings.LiquidGlass?.ValueEquals(settings.LiquidGlass) ?? (settings.LiquidGlass == null));

        _settings = settings.Clone();
        if (glassChanged)
        {
            _window?.ApplySettings(_settings);
        }

        if (_hwnd == IntPtr.Zero) return;

        if (spotlightChanged || (!IsHotkeyRegistered && settings.EnableSpotlight))
        {
            DisableHotkey();

            if (!settings.EnableSpotlight)
            {
                _window?.HideSpotlight();
                return;
            }

            _nativeRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_SPACE);
            if (_nativeRegistered)
            {
                RuntimeLog.Log(LogTag, "Alt+Space registered with Windows");
                if (!EnsureKeyboardHook())
                    RuntimeLog.Warn(LogTag, "Global Escape shortcut is unavailable");
                return;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == 1409 && EnsureKeyboardHook())
            {
                RuntimeLog.Warn(LogTag,
                    "Alt+Space is owned by another app; keyboard fallback enabled");
                return;
            }

            RuntimeLog.Warn(LogTag,
                $"Could not enable Alt+Space (Win32={error})");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            // Leave the native message callback before activation/layout work.
            _source?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input, (Action)ToggleSpotlight);
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
        RuntimeLog.Warn(LogTag,
            $"Could not install keyboard hook (Win32={Marshal.GetLastWin32Error()})");
        return false;
    }

    private bool TryHandleEscapeHook(KBDLLHOOKSTRUCT key, int message)
    {
        if (!IsEscapeKey(key.vkCode))
            return false;

        if (_escapeDown && message is WM_KEYUP or WM_SYSKEYUP)
        {
            _escapeDown = false;
            return true;
        }

        if (_window?.IsSpotlightOpen == true && message is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            if (!_escapeDown)
            {
                _escapeDown = true;
                _source?.Dispatcher.BeginInvoke(_window.HandleGlobalEscape);
            }
            return true;
        }

        return false;
    }

    private bool TryHandleFallbackHotkey(KBDLLHOOKSTRUCT key, int message)
    {
        if (_fallbackSpaceDown && key.vkCode == VK_SPACE && message is WM_KEYUP or WM_SYSKEYUP)
        {
            _fallbackSpaceDown = false;
            return true;
        }

        if (!_nativeRegistered && IsAltSpaceKey(key.vkCode, key.flags) && message is WM_KEYDOWN or WM_SYSKEYDOWN)
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
            return true;
        }

        return false;
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int message = wParam.ToInt32();

            if (TryHandleEscapeHook(key, message) || TryHandleFallbackHotkey(key, message))
                return new IntPtr(1);
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    internal void ToggleSpotlight()
    {
        if (_disposed || _settings?.EnableSpotlight != true) return;
        if (_window == null)
        {
            _window = _windowFactory();
            if (_host != null) _window.Owner = _host;
            if (_settings != null) _window.ApplySettings(_settings);
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
