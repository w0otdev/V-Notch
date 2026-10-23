using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

internal enum SpotlightKeyboardAction { None, Suppress, Toggle, Escape }

/// <summary>Hook-thread key state with immutable configuration published by the UI thread.</summary>
internal sealed class SpotlightKeyboardState
{
    private sealed record Configuration(bool Enabled, bool Fallback, bool Open, int EscapeSession);
    private Configuration _configuration = new(false, false, false, 0);
    private Configuration? _observed;
    private bool _spaceDown;
    private bool _escapeDown;
    private uint _lastSpaceTime;

    internal void Configure(bool enabled, bool fallback, bool open)
    {
        var previous = Volatile.Read(ref _configuration);
        if (previous.Enabled == enabled && previous.Fallback == fallback && previous.Open == open) return;
        Volatile.Write(ref _configuration, new Configuration(enabled, fallback, open,
            previous.EscapeSession + (previous.Open != open || previous.Enabled != enabled ? 1 : 0)));
    }

    internal SpotlightKeyboardAction Process(KBDLLHOOKSTRUCT key, int message)
    {
        var current = Volatile.Read(ref _configuration);
        if (_observed?.EscapeSession != current.EscapeSession) _escapeDown = false;
        if (_observed?.Enabled != current.Enabled || _observed?.Fallback != current.Fallback)
        {
            _spaceDown = false;
            _lastSpaceTime = 0;
        }
        _observed = current;
        if (!current.Enabled) return SpotlightKeyboardAction.None;

        if (SpotlightController.IsEscapeKey(key.vkCode))
        {
            if (_escapeDown && message is WM_KEYUP or WM_SYSKEYUP)
            {
                _escapeDown = false;
                return SpotlightKeyboardAction.Suppress;
            }
            if (current.Open && message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (_escapeDown) return SpotlightKeyboardAction.Suppress;
                _escapeDown = true;
                return SpotlightKeyboardAction.Escape;
            }
        }
        if (_spaceDown && key.vkCode == VK_SPACE && message is WM_KEYUP or WM_SYSKEYUP)
        {
            _spaceDown = false;
            return SpotlightKeyboardAction.Suppress;
        }
        if (current.Fallback && SpotlightController.IsAltSpaceKey(key.vkCode, key.flags)
            && message is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            bool dispatch = SpotlightController.ShouldDispatchFallbackToggle(_spaceDown, _lastSpaceTime, key.time);
            _spaceDown = true;
            _lastSpaceTime = key.time;
            return dispatch ? SpotlightKeyboardAction.Toggle : SpotlightKeyboardAction.Suppress;
        }
        return SpotlightKeyboardAction.None;
    }
}
