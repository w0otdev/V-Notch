using System.Runtime.InteropServices;

namespace VNotch.Services;

internal sealed partial class MediaTimelineClassifier
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTimelineClassifierState
    {
        public long LastEndTicks;
        public long LastEndObservedUtcTicks;
        public byte HasLastEndObserved;
        public int GrowthSamples;
        public byte IsLive;
        public byte WasPlaying;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timeline_classifier_observe(
        ref VnTimelineClassifierState state,
        byte identityChanged,
        long endTimeTicks,
        long seekWindowStartTicks,
        long nowUtcTicks,
        byte isPlaying,
        out byte outIsLive);

    private string _identity = "";
    private VnTimelineClassifierState _state;

    public bool Observe(string identity, TimeSpan endTime, TimeSpan seekWindowStart, DateTimeOffset nowUtc, bool isPlaying)
    {
        string normalizedIdentity = identity ?? "";
        bool identityChanged = !string.Equals(_identity, normalizedIdentity, StringComparison.Ordinal);
        _identity = normalizedIdentity;

        _ = vn_timeline_classifier_observe(
            ref _state,
            identityChanged ? (byte)1 : (byte)0,
            endTime.Ticks,
            seekWindowStart.Ticks,
            nowUtc.UtcTicks,
            isPlaying ? (byte)1 : (byte)0,
            out byte isLive);

        return isLive != 0;
    }

    public void Reset()
    {
        _identity = "";
        _state = default;
    }
}
