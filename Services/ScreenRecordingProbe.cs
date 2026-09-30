using System;

namespace VNotch.Services;

// Supplemental providers for recorders which bypass Windows ConsentStore.
internal sealed class ScreenRecordingProbe
{
    private readonly Func<bool>[] _providers;

    internal ScreenRecordingProbe()
    {
        var obs = new ObsRecordingProbe();
        var ffmpeg = new FfmpegRecordingProbe();
        _providers = new Func<bool>[] { BandicamRecordingProbe.IsRecording, obs.IsRecording, ffmpeg.IsRecording };
    }

    internal ScreenRecordingProbe(params Func<bool>[] providers) => _providers = providers;

    internal bool IsRecording()
    {
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
