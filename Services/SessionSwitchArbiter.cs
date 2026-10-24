using System.Runtime.InteropServices;

namespace VNotch.Services;

internal readonly struct SessionSwitchInputs
{
    public bool CurrentIsPremium { get; init; }
    public bool BestIsOsCurrent { get; init; }
    public bool BestIsRecentLatestPlayback { get; init; }
    public bool CurrentStillPlaying { get; init; }
    public bool BestHasFreshTimeline { get; init; }
    public double PendingElapsedSeconds { get; init; }
}

internal static partial class SessionSwitchArbiter
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSessionSwitchInputs
    {
        public byte CurrentIsPremium;
        public byte BestIsOsCurrent;
        public byte BestIsRecentLatestPlayback;
        public byte CurrentStillPlaying;
        public byte BestHasFreshTimeline;
        public double PendingElapsedSeconds;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_session_switch_hold_seconds(byte currentIsPremium, out double outSeconds);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_session_switch_should_hold(in VnSessionSwitchInputs inputs, out byte outShouldHold);

    public static double HoldSeconds(bool currentIsPremium)
    {
        _ = vn_session_switch_hold_seconds(currentIsPremium ? (byte)1 : (byte)0, out double sec);
        return sec;
    }

    public static bool ShouldHoldCurrentSession(in SessionSwitchInputs x)
    {
        var native = new VnSessionSwitchInputs
        {
            CurrentIsPremium = x.CurrentIsPremium ? (byte)1 : (byte)0,
            BestIsOsCurrent = x.BestIsOsCurrent ? (byte)1 : (byte)0,
            BestIsRecentLatestPlayback = x.BestIsRecentLatestPlayback ? (byte)1 : (byte)0,
            CurrentStillPlaying = x.CurrentStillPlaying ? (byte)1 : (byte)0,
            BestHasFreshTimeline = x.BestHasFreshTimeline ? (byte)1 : (byte)0,
            PendingElapsedSeconds = x.PendingElapsedSeconds,
        };
        _ = vn_session_switch_should_hold(in native, out byte shouldHold);
        return shouldHold != 0;
    }
}
