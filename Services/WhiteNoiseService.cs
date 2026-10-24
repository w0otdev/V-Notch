using System.IO;
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

    private static int _playingKind = -1;
    private static float _volume = DefaultVolume;
    private static bool _muted;
    private static bool _syncWithTimer;

    static WhiteNoiseService() => LoadPreferences();

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
            SavePreferences();
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
            SavePreferences();
            StateChanged?.Invoke();
        }
    }

    private static float EffectiveVolume => _muted ? 0f : _volume;

    private static void ApplyVolume() => vn_noise_set_volume(EffectiveVolume);

    internal static void Play(int kind)
    {
        if (kind < 0 || kind >= Catalog.Count) return;
        if (vn_noise_play(kind, EffectiveVolume) != 0) return;
        _playingKind = kind;
        StateChanged?.Invoke();
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
        }
    }

    private static void SavePreferences()
    {
        try
        {
            string path = PreferencesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"sync={(_syncWithTimer ? 1 : 0)}\nvolume={_volume:0.###}\n"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
