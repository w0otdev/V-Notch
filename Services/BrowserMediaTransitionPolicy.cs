using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class BrowserMediaTransitionPolicy
{
    internal static readonly TimeSpan LikelyAdMaximumDuration = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan LikelyAdQuarantineWindow = TimeSpan.FromSeconds(45);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnBrowserAdTransitionDecision
    {
        public byte ShouldHold;
        public long TransitionStartedUtcTicks;
        public byte WasTransitionActive;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_browser_media_eval_likely_youtube_ad(
        byte isBrowserSession,
        byte hasStableTrack,
        char* lastSourcePtr, int lastSourceLen,
        char* lastSessionKeyPtr, int lastSessionKeyLen,
        char* curSessionKeyPtr, int curSessionKeyLen,
        char* curTrackPtr, int curTrackLen,
        long curDurationTicks,
        byte hasYouTubeWindow,
        byte curTrackMatchesYouTubeWindow,
        long transitionStartedUtcTicks,
        long nowUtcTicks,
        out VnBrowserAdTransitionDecision outDecision);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_browser_media_eval_youtube_junk_metadata(
        byte isBrowserSession,
        byte hasStableTrack,
        char* lastSourcePtr, int lastSourceLen,
        byte hasYouTubeWindow,
        long transitionStartedUtcTicks,
        long nowUtcTicks,
        out VnBrowserAdTransitionDecision outDecision);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_browser_media_should_carry_youtube_source(
        byte isBrowserSession,
        char* hintPtr, int hintLen,
        char* lastSourcePtr, int lastSourceLen,
        char* stableSourcePtr, int stableSourceLen,
        byte isCompletingAdTransition,
        out byte outCarry);

#pragma warning disable S107
    public static unsafe BrowserAdTransitionDecision EvaluateLikelyYouTubeAd(
        bool isBrowserSession,
        bool hasStableTrack,
        string? lastSource,
        string? lastPublishedSessionInstanceKey,
        string? currentSessionInstanceKey,
        string? currentTrack,
        TimeSpan currentDuration,
        bool hasYouTubeWindow,
        bool currentTrackMatchesYouTubeWindow,
        DateTime transitionStartedUtc,
        DateTime nowUtc)
    {
        string lSrc = lastSource ?? string.Empty;
        string lKey = lastPublishedSessionInstanceKey ?? string.Empty;
        string cKey = currentSessionInstanceKey ?? string.Empty;
        string cTr = currentTrack ?? string.Empty;

        fixed (char* pLSrc = lSrc)
        fixed (char* pLKey = lKey)
        fixed (char* pCKey = cKey)
        fixed (char* pCTr = cTr)
        {
            _ = vn_browser_media_eval_likely_youtube_ad(
                isBrowserSession ? (byte)1 : (byte)0,
                hasStableTrack ? (byte)1 : (byte)0,
                pLSrc, lSrc.Length,
                pLKey, lKey.Length,
                pCKey, cKey.Length,
                pCTr, cTr.Length,
                currentDuration.Ticks,
                hasYouTubeWindow ? (byte)1 : (byte)0,
                currentTrackMatchesYouTubeWindow ? (byte)1 : (byte)0,
                transitionStartedUtc == DateTime.MinValue ? 0 : transitionStartedUtc.Ticks,
                nowUtc.Ticks,
                out VnBrowserAdTransitionDecision dec);

            DateTime started = dec.TransitionStartedUtcTicks == 0
                ? DateTime.MinValue
                : new DateTime(dec.TransitionStartedUtcTicks, transitionStartedUtc.Kind);
            return new BrowserAdTransitionDecision(dec.ShouldHold != 0, started, dec.WasTransitionActive != 0);
        }
    }
#pragma warning restore S107

    public static unsafe BrowserAdTransitionDecision EvaluateYouTubeJunkMetadata(
        bool isBrowserSession,
        bool hasStableTrack,
        string? lastSource,
        bool hasYouTubeWindow,
        DateTime transitionStartedUtc,
        DateTime nowUtc)
    {
        string lSrc = lastSource ?? string.Empty;
        fixed (char* pLSrc = lSrc)
        {
            _ = vn_browser_media_eval_youtube_junk_metadata(
                isBrowserSession ? (byte)1 : (byte)0,
                hasStableTrack ? (byte)1 : (byte)0,
                pLSrc, lSrc.Length,
                hasYouTubeWindow ? (byte)1 : (byte)0,
                transitionStartedUtc == DateTime.MinValue ? 0 : transitionStartedUtc.Ticks,
                nowUtc.Ticks,
                out VnBrowserAdTransitionDecision dec);

            DateTime started = dec.TransitionStartedUtcTicks == 0
                ? DateTime.MinValue
                : new DateTime(dec.TransitionStartedUtcTicks, transitionStartedUtc.Kind);
            return new BrowserAdTransitionDecision(dec.ShouldHold != 0, started, dec.WasTransitionActive != 0);
        }
    }

    public static unsafe bool ShouldCarryYouTubeSource(
        bool isBrowserSession,
        string? currentBrowserPlatformHint,
        string? lastSource,
        string? stableSource,
        bool isCompletingAdTransition)
    {
        string hint = currentBrowserPlatformHint ?? string.Empty;
        string lSrc = lastSource ?? string.Empty;
        string sSrc = stableSource ?? string.Empty;

        fixed (char* pH = hint)
        fixed (char* pL = lSrc)
        fixed (char* pS = sSrc)
        {
            _ = vn_browser_media_should_carry_youtube_source(
                isBrowserSession ? (byte)1 : (byte)0,
                pH, hint.Length,
                pL, lSrc.Length,
                pS, sSrc.Length,
                isCompletingAdTransition ? (byte)1 : (byte)0,
                out byte carry);
            return carry != 0;
        }
    }
}

internal readonly record struct BrowserAdTransitionDecision(
    bool ShouldHold,
    DateTime TransitionStartedUtc,
    bool WasTransitionActive);
