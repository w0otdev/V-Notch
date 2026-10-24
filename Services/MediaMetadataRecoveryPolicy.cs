using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class MediaMetadataRecoveryPolicy
{
    internal static readonly TimeSpan TransientSessionGapGrace = TimeSpan.FromSeconds(2);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTransientSessionGapDecision
    {
        public byte ShouldHold;
        public long GapStartedUtcTicks;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_recovery_select_detection_mode(
        byte isAnyPlaying, char* curTrackPtr, int curTrackLen, byte isThrottled, byte isSessionGapRecovery, out int outMode);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_recovery_get_poll_interval_ticks(long elapsedTicks, out long outIntervalTicks);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_recovery_can_use_browser_title_fallback(
        byte isSpotifyPlaying, char* mediaSourcePtr, int mediaSourceLen, out byte outCanUse);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_media_recovery_should_continue_after_junk_title(
        byte isJunkTitle, byte isBrowserSession, out byte outContinue);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_recovery_should_suppress_active_empty_publish(
        byte isAnyPlaying, char* curTrackPtr, int curTrackLen, out byte outSuppress);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_recovery_eval_transient_session_gap(
        byte hasResolvedSession, byte hasStableTrack, char* lastSourcePtr, int lastSourceLen,
        long gapStartedUtcTicks, long nowUtcTicks, out VnTransientSessionGapDecision outDecision);

    public static unsafe DetectionMode SelectDetectionMode(
        bool isAnyMediaPlaying,
        string? currentTrack,
        bool isThrottled,
        bool isSessionGapRecovery = false)
    {
        string tr = currentTrack ?? string.Empty;
        fixed (char* pTr = tr)
        {
            _ = vn_media_recovery_select_detection_mode(
                isAnyMediaPlaying ? (byte)1 : (byte)0,
                pTr, tr.Length,
                isThrottled ? (byte)1 : (byte)0,
                isSessionGapRecovery ? (byte)1 : (byte)0,
                out int mode);
            return (DetectionMode)mode;
        }
    }

    public static TimeSpan GetAwaitingMetadataPollInterval(TimeSpan elapsed)
    {
        _ = vn_media_recovery_get_poll_interval_ticks(elapsed.Ticks, out long ticks);
        return TimeSpan.FromTicks(ticks);
    }

    public static unsafe bool CanUseBrowserWindowTitleFallback(bool isSpotifyPlaying, string? mediaSource)
    {
        string src = mediaSource ?? string.Empty;
        fixed (char* pSrc = src)
        {
            _ = vn_media_recovery_can_use_browser_title_fallback(
                isSpotifyPlaying ? (byte)1 : (byte)0, pSrc, src.Length, out byte canUse);
            return canUse != 0;
        }
    }

    public static bool ShouldContinueAfterJunkTitle(bool isJunkTitle, bool isBrowserSession)
    {
        _ = vn_media_recovery_should_continue_after_junk_title(
            isJunkTitle ? (byte)1 : (byte)0, isBrowserSession ? (byte)1 : (byte)0, out byte cont);
        return cont != 0;
    }

    public static unsafe bool ShouldSuppressActiveEmptyPublish(bool isAnyMediaPlaying, string? currentTrack)
    {
        string tr = currentTrack ?? string.Empty;
        fixed (char* pTr = tr)
        {
            _ = vn_media_recovery_should_suppress_active_empty_publish(
                isAnyMediaPlaying ? (byte)1 : (byte)0, pTr, tr.Length, out byte supp);
            return supp != 0;
        }
    }

    public static unsafe TransientSessionGapDecision EvaluateTransientSessionGap(
        bool hasResolvedSession,
        bool hasStableTrack,
        string? lastSource,
        DateTime gapStartedUtc,
        DateTime nowUtc)
    {
        string src = lastSource ?? string.Empty;
        fixed (char* pSrc = src)
        {
            _ = vn_media_recovery_eval_transient_session_gap(
                hasResolvedSession ? (byte)1 : (byte)0,
                hasStableTrack ? (byte)1 : (byte)0,
                pSrc, src.Length,
                gapStartedUtc == DateTime.MinValue ? 0 : gapStartedUtc.Ticks,
                nowUtc.Ticks,
                out VnTransientSessionGapDecision dec);

            DateTime started = dec.GapStartedUtcTicks == 0
                ? DateTime.MinValue
                : new DateTime(dec.GapStartedUtcTicks, gapStartedUtc.Kind);
            return new TransientSessionGapDecision(dec.ShouldHold != 0, started);
        }
    }
}

internal readonly record struct TransientSessionGapDecision(
    bool ShouldHold,
    DateTime GapStartedUtc);
