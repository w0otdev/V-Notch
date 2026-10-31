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

internal sealed partial class SpotlightController : ISpotlightController, IAsyncDisposable
{
    private const string LogTag = "SPOTLIGHT-HOTKEY";
    private const int HotkeyId = 0x564E;


    private readonly Func<SpotlightWindow> _windowFactory;
    private SpotlightWindow? _window;
    private Window? _host;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private DedicatedKeyboardHook? _keyboardHook;
    private readonly SpotlightKeyboardState _keyboardState = new();
    private bool _nativeRegistered;
    private NotchSettings? _settings;
    private bool _disposed;
    private bool _preparationQueued;
    private int _hostGeneration;

    public bool IsHotkeyRegistered => _nativeRegistered || _keyboardHook?.IsInstalled == true;

    public SpotlightController(Func<SpotlightWindow> windowFactory)
    {
        _windowFactory = windowFactory;
    }

    public void Initialize(Window host, NotchSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host == host) return;
        DetachHost();

        _host = host;
        host.Closed += Host_Closed;
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
        bool historyChanged = _settings == null || _settings.EnableSpotlightHistory != settings.EnableSpotlightHistory;
        bool languageChanged = _settings == null || !string.Equals(_settings.Language, settings.Language, StringComparison.Ordinal);
        bool glassChanged = _settings == null
            || !string.Equals(_settings.NotchStyle, settings.NotchStyle, StringComparison.OrdinalIgnoreCase)
            || !(_settings.LiquidGlass?.ValueEquals(settings.LiquidGlass) ?? (settings.LiquidGlass == null));

        bool aiChanged = _settings == null
            || _settings.SpotlightDefaultAi != settings.SpotlightDefaultAi
            || _settings.SpotlightAiWordsPerSecond != settings.SpotlightAiWordsPerSecond
            || !string.Equals(_settings.SpotlightAiProvider, settings.SpotlightAiProvider, StringComparison.Ordinal)
            || VNotch.Services.Spotlight.SpotlightAiService.Providers.Any(provider =>
                VNotch.Services.Spotlight.SpotlightAiService.Configuration(_settings, provider)
                != VNotch.Services.Spotlight.SpotlightAiService.Configuration(settings, provider));

        _settings = settings.Clone();
        if (glassChanged || historyChanged || languageChanged || aiChanged)
        {
            _window?.ApplySettings(_settings);
        }
        if (settings.EnableSpotlight && (spotlightChanged || glassChanged))
            QueuePreparation();

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
                UpdateKeyboardHook();
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
        if (msg == WM_HOTKEY && wParam.ToInt64() == HotkeyId)
        {
            handled = true;
            // Leave the native message callback before activation/layout work.
            int generation = _hostGeneration;
            _source?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
                {
                    if (generation == _hostGeneration) ToggleSpotlight();
                }));
        }
        return IntPtr.Zero;
    }

    internal static bool IsAltSpaceKey(uint vkCode, uint flags)
    {
        vn_spotlight_is_alt_space_key(vkCode, flags, out byte match);
        return match != 0;
    }

    internal static bool IsEscapeKey(uint vkCode)
    {
        vn_spotlight_is_escape_key(vkCode, out byte match);
        return match != 0;
    }

    internal static bool ShouldDispatchFallbackToggle(
        bool spaceIsAlreadyDown,
        uint lastSpaceEventTime,
        uint currentTime)
    {
        vn_spotlight_should_dispatch_fallback_toggle(
            spaceIsAlreadyDown ? (byte)1 : (byte)0,
            lastSpaceEventTime,
            currentTime,
            out byte dispatch);
        return dispatch != 0;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_is_alt_space_key(uint vk_code, uint flags, out byte out_match);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_is_escape_key(uint vk_code, out byte out_match);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_should_dispatch_fallback_toggle(
        byte space_already_down, uint last_space_time, uint current_time, out byte out_dispatch);

    private bool EnsureKeyboardHook()
    {
        _keyboardState.Configure(true, !_nativeRegistered, _window?.IsSpotlightOpen == true);
        if (_keyboardHook?.IsInstalled == true) return true;
        _keyboardHook?.Dispose();
        var dispatcher = _host?.Dispatcher;
        if (dispatcher == null) return false;
        int generation = _hostGeneration;
        _keyboardHook = new DedicatedKeyboardHook((key, message) =>
        {
            var action = _keyboardState.Process(key, message);
            if (action is SpotlightKeyboardAction.Toggle or SpotlightKeyboardAction.Escape)
            {
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
                {
                    if (_disposed || generation != _hostGeneration) return;
                    if (action == SpotlightKeyboardAction.Toggle) ToggleSpotlight();
                    else if (_window?.IsSpotlightOpen == true) _window.HandleGlobalEscape();
                }));
            }
            return action != SpotlightKeyboardAction.None;
        });
        if (_keyboardHook.IsInstalled) return true;
        RuntimeLog.Warn(LogTag,
            $"Could not install keyboard hook (Win32={_keyboardHook.InstallationError})");
        _keyboardHook.Dispose();
        _keyboardHook = null;
        return false;
    }

    private void UpdateKeyboardHook()
    {
        // Publish open/closed transitions even while retaining a fallback hook.
        // A missing key-up must not poison Escape in the next Spotlight session.
        _keyboardState.Configure(!_disposed && _settings?.EnableSpotlight == true && _hwnd != IntPtr.Zero,
            !_nativeRegistered, _window?.IsSpotlightOpen == true);
        if (_disposed || _settings?.EnableSpotlight != true || _hwnd == IntPtr.Zero)
        {
            RemoveKeyboardHook();
            return;
        }
        if (!_nativeRegistered || _window?.IsSpotlightOpen == true)
        {
            EnsureKeyboardHook();
            return;
        }
        RemoveKeyboardHook();
    }

    private void Window_OpenStateChanged(object? sender, EventArgs e) => UpdateKeyboardHook();
    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateKeyboardHook();

    private SpotlightWindow CreateWindow()
    {
        var window = _windowFactory();
        window.OpenStateChanged += Window_OpenStateChanged;
        window.IsVisibleChanged += Window_IsVisibleChanged;
        return window;
    }

    private void RemoveKeyboardHook()
    {
        _keyboardState.Configure(false, false, false);
        _keyboardHook?.Dispose();
        _keyboardHook = null;
    }

    internal void ToggleSpotlight()
    {
        if (_host is MainWindow { IsGreetingInteractionBlocked: true }) return;
        if (_disposed || _host == null || _settings?.EnableSpotlight != true) return;
        if (_window == null)
        {
            _window = CreateWindow();
            if (_host != null) _window.Owner = _host;
            if (_settings != null) _window.ApplySettings(_settings);
        }
        _window.ToggleFromHotkey();
        UpdateKeyboardHook();
    }

    private void QueuePreparation()
    {
        if (_preparationQueued || _host == null) return;
        _preparationQueued = true;
        int generation = _hostGeneration;
        _host.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
        {
            if (generation != _hostGeneration) return;
            _preparationQueued = false;
            if (_disposed || _host == null || _settings?.EnableSpotlight != true) return;
            try
            {
                if (_window == null)
                {
                    _window = CreateWindow();
                    _window.Owner = _host;
                    _window.ApplySettings(_settings);
                }
                _window.PrepareGlassForOpening();
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(LogTag, $"Spotlight idle preparation skipped: {ex.Message}");
            }
        }));
    }

    private void DisableHotkey()
    {
        if (_nativeRegistered) UnregisterHotKey(_hwnd, HotkeyId);
        _nativeRegistered = false;
        RemoveKeyboardHook();
    }

    private void Host_Closed(object? sender, EventArgs e) => DetachHost();

    private void DetachHost()
    {
        ++_hostGeneration;
        _preparationQueued = false;
        DisableHotkey();
        if (_source is { IsDisposed: false }) _source.RemoveHook(WndProc);
        _source = null;
        if (_host != null) _host.Closed -= Host_Closed;
        _host = null;
        _hwnd = IntPtr.Zero;
        if (_window != null)
        {
            _window.OpenStateChanged -= Window_OpenStateChanged;
            _window.IsVisibleChanged -= Window_IsVisibleChanged;
            _window.Shutdown();
            _window = null;
        }
    }

    public void Dispose()
    {
        var window = _window;
        DisposeCore();
        window?.FlushPersistenceAsync().GetAwaiter().GetResult();
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        DetachHost();
    }

    public async ValueTask DisposeAsync()
    {
        var window = _window;
        DisposeCore();
        if (window != null) await window.FlushPersistenceAsync().ConfigureAwait(false);
    }
}
