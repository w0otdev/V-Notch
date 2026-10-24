using System.Runtime.InteropServices;
using System.Threading;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

internal enum SpotlightKeyboardAction { None, Suppress, Toggle, Escape }

/// <summary>Hook-thread key state with immutable configuration published by the UI thread.</summary>
internal sealed partial class SpotlightKeyboardState
{
    private sealed record Configuration(bool Enabled, bool Fallback, bool Open, int EscapeSession);
    private Configuration _configuration = new(false, false, false, 0);
    private byte _hasObserved;
    private byte _obsEnabled;
    private byte _obsFallback;
    private int _obsEscapeSession;
    private byte _spaceDown;
    private byte _escapeDown;
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
        vn_spotlight_kbd_process(
            current.Enabled ? (byte)1 : (byte)0,
            current.Fallback ? (byte)1 : (byte)0,
            current.Open ? (byte)1 : (byte)0,
            current.EscapeSession,
            ref _hasObserved,
            ref _obsEnabled,
            ref _obsFallback,
            ref _obsEscapeSession,
            ref _spaceDown,
            ref _escapeDown,
            ref _lastSpaceTime,
            key.vkCode,
            key.flags,
            key.time,
            message,
            out int action);
        return (SpotlightKeyboardAction)action;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotlight_kbd_process(
        byte enabled,
        byte fallback,
        byte open,
        int escape_session,
        ref byte io_has_observed,
        ref byte io_obs_enabled,
        ref byte io_obs_fallback,
        ref int io_obs_escape_session,
        ref byte io_space_down,
        ref byte io_escape_down,
        ref uint io_last_space_time,
        uint vk_code,
        uint flags,
        uint time,
        int message,
        out int out_action);
}
