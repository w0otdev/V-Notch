using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

// Native polling owns process discovery, file tails and Restart Manager sessions.
internal sealed partial class ScreenRecordingProbe
{
    private readonly Func<bool>[]? _providers;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_privacy_poll_recorders(ReadOnlySpan<Win32Interop.VnProcessEntry> entries,
        int count, byte capture, out byte active);

    internal ScreenRecordingProbe() { }
    internal ScreenRecordingProbe(params Func<bool>[] providers) => _providers = providers;

    internal bool IsRecording(Func<PrivacyProcessSnapshot>? getSnapshot = null)
    {
        if (_providers != null) return ReadInjectedProviders();
        try { return IsRecordingWithSnapshot(getSnapshot?.Invoke()); }
        catch (Exception) { return false; }
    }

    internal bool IsRecordingWithSnapshot(PrivacyProcessSnapshot? snapshot)
    {
        if (_providers != null) return ReadInjectedProviders();
        var entries = snapshot == null ? ReadOnlySpan<Win32Interop.VnProcessEntry>.Empty : snapshot.Entries;
        return vn_privacy_poll_recorders(entries, entries.Length, snapshot == null ? (byte)1 : (byte)0, out byte active) == 0 && active != 0;
    }

    private bool ReadInjectedProviders()
    {
        foreach (var provider in _providers!)
        {
            try { if (provider()) return true; }
            catch (Exception) { /* Independent providers must not freeze sensor updates. */ }
        }
        return false;
    }
}
