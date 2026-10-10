using System;

namespace VNotch.Services;

// Supplemental providers for recorders which bypass Windows ConsentStore.
internal sealed class ScreenRecordingProbe
{
    private readonly Func<bool>[] _providers = Array.Empty<Func<bool>>();
    private readonly Func<PrivacyProcessSnapshot, bool>[] _processProviders = Array.Empty<Func<PrivacyProcessSnapshot, bool>>();

    internal ScreenRecordingProbe()
    {
        var obs = new ObsRecordingProbe();
        var ffmpeg = new FfmpegRecordingProbe();
        _processProviders = new Func<PrivacyProcessSnapshot, bool>[]
            { BandicamRecordingProbe.IsRecording, obs.IsRecording, ffmpeg.IsRecording };
    }

    internal ScreenRecordingProbe(params Func<bool>[] providers) => _providers = providers;

    internal bool IsRecording(Func<PrivacyProcessSnapshot>? getSnapshot = null)
    {
        // Reuse the sensor scan's snapshot instead of enumerating every Windows
        // process separately for Bandicam, both OBS variants and FFmpeg.
        try
        {
            using var ownedSnapshot = _processProviders.Length > 0 && getSnapshot == null
                ? PrivacyProcessSnapshot.Capture() : null;
            if (_processProviders.Length > 0)
            {
                var snapshot = ownedSnapshot ?? getSnapshot!();
                foreach (var provider in _processProviders)
                {
                    try
                    {
                        if (provider(snapshot)) return true;
                    }
                    catch (Exception)
                    {
                        // One inaccessible recorder must not prevent other providers.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Failed process discovery is unknown recording evidence. Sensor
            // state from the same privacy scan must still be published.
        }
        foreach (var provider in _providers)
        {
            try
            {
                if (provider()) return true;
            }
            catch (Exception)
            {
                // Best-effort providers fail independently. Never freeze mic/camera/location
                // at their previous state because one recorder is inaccessible or has exited.
            }
        }
        return false;
    }
}
