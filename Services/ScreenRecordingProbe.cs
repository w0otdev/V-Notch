using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

// Supplemental providers for recorders which bypass Windows ConsentStore.
internal sealed partial class ScreenRecordingProbe
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_screen_recording_probe_should_capture_owned_snapshot")]
    private static partial int NativeShouldCaptureOwnedSnapshot(
        int processProviderCount,
        byte hasGetSnapshotDelegate,
        out byte outShouldCapture);

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
            NativeShouldCaptureOwnedSnapshot(
                _processProviders.Length,
                getSnapshot != null ? (byte)1 : (byte)0,
                out byte shouldCapture);
            using var ownedSnapshot = shouldCapture != 0
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
