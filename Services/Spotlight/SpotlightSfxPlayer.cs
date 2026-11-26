using System.IO;
using System.Runtime.InteropServices;
using VNotch.Interop;

namespace VNotch.Services.Spotlight;

internal sealed partial class SpotlightSfxPlayer
{
    private const int MinClickIntervalMs = 220;
    private const double VolumeBoost = 1.35;

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_sfx_load(string path, int pathLength, out int hresult);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_spotlight_sfx_play(float volume);

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_spotlight_sfx_stop();

    [LibraryImport(NativeAbi.LibraryName)]
    private static partial int vn_spotlight_sfx_is_loaded(out byte loaded);

    private string? _clickAudioPath;
    private long _lastPlayTick;

    public void PlayClick(double interactionSoundVolume)
    {
        if (interactionSoundVolume <= 0) return;

        long now = Environment.TickCount64;
        if (now - _lastPlayTick < MinClickIntervalMs) return;
        _lastPlayTick = now;

        try
        {
            if (vn_spotlight_sfx_is_loaded(out byte loaded) != 0 || loaded == 0)
            {
                _clickAudioPath ??= ResolveClickAudioPath();
                if (_clickAudioPath == null) return;
                if (vn_spotlight_sfx_load(_clickAudioPath, _clickAudioPath.Length, out _) != 0) return;
            }

            float scaledVolume = (float)Math.Clamp(interactionSoundVolume * VolumeBoost, 0.0, 1.0);
            _ = vn_spotlight_sfx_play(scaledVolume);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SPOTLIGHT-SFX", $"Spotlight click sound failed: {ex.Message}");
        }
    }

    public static void Stop() => _ = vn_spotlight_sfx_stop();

    private static string? ResolveClickAudioPath()
    {
        string? directory = AppDomain.CurrentDomain.BaseDirectory;
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
        {
            string m4a = Path.Combine(directory, "Assets", "SPL_CLICK_SFX.m4a");
            if (File.Exists(m4a)) return m4a;

            string mp3 = Path.Combine(directory, "Assets", "SPL_CLICK_SFX.mp3");
            if (File.Exists(mp3)) return mp3;

            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }
}
