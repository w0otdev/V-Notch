using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>
/// Thin facade over the native procedural noise generator (vn_noise_*). Sounds are synthesized
/// in real time, so there is nothing to bundle or download and playback never loops audibly.
/// </summary>
internal static partial class WhiteNoiseService
{
    internal sealed record Sound(int Kind, string LocKey);

    internal static readonly IReadOnlyList<Sound> Catalog =
    [
        new(0, "whitenoise.white"),
        new(1, "whitenoise.pink"),
        new(2, "whitenoise.brown"),
        new(3, "whitenoise.fan"),
        new(4, "whitenoise.rain"),
        new(5, "whitenoise.ocean"),
        new(6, "whitenoise.wind"),
        new(7, "whitenoise.brook"),
    ];

    internal const float DefaultVolume = 0.3f;
    private const float TimerEndFadeSeconds = 3f;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_noise_play(int kind, float volume);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_noise_set_volume(float volume);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_noise_stop();

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_noise_stop_fade(float seconds);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_noise_set_failure_callback(delegate* unmanaged[Stdcall]<int, int, void> callback);

    private static int _playingKind = -1;
    private static int _failedKind = -1;
    private static float _volume = DefaultVolume;
    private static bool _muted;
    private static bool _syncWithTimer;
    private static readonly CoalescingActionQueue SaveQueue = new();

    static WhiteNoiseService()
    {
        LoadPreferences();
        RegisterFailureCallback();
    }

    internal static event Action? StateChanged;

    internal static int PlayingKind => _playingKind;

    internal static float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_muted && _volume > 0f) _muted = false;
            ApplyVolume();
            SaveQueue.Post("prefs", SavePreferences);
            StateChanged?.Invoke();
        }
    }

    internal static bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            ApplyVolume();
            StateChanged?.Invoke();
        }
    }

    /// <summary>When enabled, noise fades out by itself once the countdown timer finishes.</summary>
    internal static bool SyncWithTimer
    {
        get => _syncWithTimer;
        set
        {
            if (_syncWithTimer == value) return;
            _syncWithTimer = value;
            SaveQueue.Post("prefs", SavePreferences);
            StateChanged?.Invoke();
        }
    }

    private static float EffectiveVolume => _muted ? 0f : _volume;

    private static void ApplyVolume() => vn_noise_set_volume(EffectiveVolume);

    internal static void Play(int kind)
    {
        if (kind < 0 || kind >= Catalog.Count) return;
        Volatile.Write(ref _failedKind, -1);
        if (vn_noise_play(kind, EffectiveVolume) != 0) return;
        Volatile.Write(ref _playingKind, kind);
        // The native worker may already have failed (for example waveOutOpen) before the line above ran.
        if (Volatile.Read(ref _failedKind) == kind) Interlocked.CompareExchange(ref _playingKind, -1, kind);
        StateChanged?.Invoke();
    }

    private static unsafe void RegisterFailureCallback()
    {
        try
        {
            vn_noise_set_failure_callback(&OnNativePlaybackFailed);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            RuntimeLog.Log("WhiteNoise", $"Native playback failure callback is unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Raised on the native player thread when the device could not be opened or playback broke after
    /// vn_noise_play had already succeeded. Clears the playing state so the UI stops showing the sound as active.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnNativePlaybackFailed(int kind, int errorCode)
    {
        try
        {
            Volatile.Write(ref _failedKind, kind);
            RuntimeLog.Log("WhiteNoise", $"Playback of kind {kind} stopped (native error {errorCode}).");
            if (Interlocked.CompareExchange(ref _playingKind, -1, kind) != kind) return;
            StateChanged?.Invoke();
        }
        catch (Exception)
        {
            // An exception must never cross the native callback boundary.
        }
    }
    internal static void Toggle(Sound sound)
    {
        if (_playingKind == sound.Kind) Pause();
        else Play(sound.Kind);
    }

    internal static void Pause()
    {
        if (_playingKind < 0) return;
        vn_noise_stop();
        _playingKind = -1;
        StateChanged?.Invoke();
    }

    /// <summary>Called when the countdown finishes. Fades noise out if sync is enabled.</summary>
    internal static void OnTimerCompleted()
    {
        if (!_syncWithTimer || _playingKind < 0) return;
        vn_noise_stop_fade(TimerEndFadeSeconds);
        _playingKind = -1;
        StateChanged?.Invoke();
    }

    private static string PreferencesPath => Path.Combine(AppPaths.LocalAppDataRoot, "WhiteNoise", "preferences.txt");

    private static void LoadPreferences()
    {
        try
        {
            string path = PreferencesPath;
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                string key = line[..split].Trim();
                string value = line[(split + 1)..].Trim();
                if (key == "sync") _syncWithTimer = value == "1";
                else if (key == "volume" && float.TryParse(value, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out float volume))
                    _volume = Math.Clamp(volume, 0f, 1f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore unreadable preferences file.
        }
    }

    private static void SavePreferences()
    {
        string path = PreferencesPath;
        string tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"sync={(_syncWithTimer ? 1 : 0)}\nvolume={_volume:0.###}\n"));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { /* Ignore temp delete error */ }
        }
    }
}
