using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Interop;

internal static partial class MediaPolicyNative
{
    internal readonly record struct YouTubeAdEvaluationRequest(
        bool IsBrowserSession,
        bool HasStableTrack,
        string? LastSource,
        string? LastPublishedSessionInstanceKey,
        string? CurrentSessionInstanceKey,
        string? CurrentTrack,
        TimeSpan CurrentDuration,
        bool HasYouTubeWindow,
        bool CurrentTrackMatchesYouTubeWindow,
        DateTime TransitionStartedUtc,
        DateTime NowUtc);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnBrowserAdTransitionDecision
    {
        public byte ShouldHold;
        public long TransitionStartedUtcTicks;
        public byte WasTransitionActive;
    }

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_browser_media_eval_likely_youtube_ad(
        byte isBrowserSession,
        byte hasStableTrack,
        ReadOnlySpan<char> lastSourcePtr, int lastSourceLen,
        ReadOnlySpan<char> lastSessionKeyPtr, int lastSessionKeyLen,
        ReadOnlySpan<char> curSessionKeyPtr, int curSessionKeyLen,
        ReadOnlySpan<char> curTrackPtr, int curTrackLen,
        long curDurationTicks,
        byte hasYouTubeWindow,
        byte curTrackMatchesYouTubeWindow,
        long transitionStartedUtcTicks,
        long nowUtcTicks,
        out VnBrowserAdTransitionDecision outDecision);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_browser_media_eval_youtube_junk_metadata(
        byte isBrowserSession,
        byte hasStableTrack,
        ReadOnlySpan<char> lastSourcePtr, int lastSourceLen,
        byte hasYouTubeWindow,
        long transitionStartedUtcTicks,
        long nowUtcTicks,
        out VnBrowserAdTransitionDecision outDecision);

    [LibraryImport(NativeAbi.LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_browser_media_should_carry_youtube_source(
        byte isBrowserSession,
        ReadOnlySpan<char> hintPtr, int hintLen,
        ReadOnlySpan<char> lastSourcePtr, int lastSourceLen,
        ReadOnlySpan<char> stableSourcePtr, int stableSourceLen,
        byte isCompletingAdTransition,
        out byte outCarry);

    public static BrowserAdTransitionDecision EvaluateLikelyYouTubeAd(in YouTubeAdEvaluationRequest request)
    {
        ReadOnlySpan<char> lastSource = request.LastSource.AsSpan();
        ReadOnlySpan<char> lastSessionKey = request.LastPublishedSessionInstanceKey.AsSpan();
        ReadOnlySpan<char> currentSessionKey = request.CurrentSessionInstanceKey.AsSpan();
        ReadOnlySpan<char> currentTrack = request.CurrentTrack.AsSpan();

        int result = vn_browser_media_eval_likely_youtube_ad(
            request.IsBrowserSession ? (byte)1 : (byte)0,
            request.HasStableTrack ? (byte)1 : (byte)0,
            lastSource, lastSource.Length,
            lastSessionKey, lastSessionKey.Length,
            currentSessionKey, currentSessionKey.Length,
            currentTrack, currentTrack.Length,
            request.CurrentDuration.Ticks,
            request.HasYouTubeWindow ? (byte)1 : (byte)0,
            request.CurrentTrackMatchesYouTubeWindow ? (byte)1 : (byte)0,
            request.TransitionStartedUtc == DateTime.MinValue ? 0 : request.TransitionStartedUtc.Ticks,
            request.NowUtc.Ticks,
            out VnBrowserAdTransitionDecision decision);
        NativeResult.RequireSuccess(result, nameof(EvaluateLikelyYouTubeAd));

        return ToDecision(decision, request.TransitionStartedUtc.Kind);
    }

    public static BrowserAdTransitionDecision EvaluateLikelyYouTubeAd(
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
        DateTime nowUtc) =>
        EvaluateLikelyYouTubeAd(new YouTubeAdEvaluationRequest(
            isBrowserSession,
            hasStableTrack,
            lastSource,
            lastPublishedSessionInstanceKey,
            currentSessionInstanceKey,
            currentTrack,
            currentDuration,
            hasYouTubeWindow,
            currentTrackMatchesYouTubeWindow,
            transitionStartedUtc,
            nowUtc));

    public static BrowserAdTransitionDecision EvaluateYouTubeJunkMetadata(
        bool isBrowserSession,
        bool hasStableTrack,
        string? lastSource,
        bool hasYouTubeWindow,
        DateTime transitionStartedUtc,
        DateTime nowUtc)
    {
        ReadOnlySpan<char> source = lastSource.AsSpan();
        int result = vn_browser_media_eval_youtube_junk_metadata(
            isBrowserSession ? (byte)1 : (byte)0,
            hasStableTrack ? (byte)1 : (byte)0,
            source, source.Length,
            hasYouTubeWindow ? (byte)1 : (byte)0,
            transitionStartedUtc == DateTime.MinValue ? 0 : transitionStartedUtc.Ticks,
            nowUtc.Ticks,
            out VnBrowserAdTransitionDecision decision);
        NativeResult.RequireSuccess(result, nameof(EvaluateYouTubeJunkMetadata));

        return ToDecision(decision, transitionStartedUtc.Kind);
    }

    public static bool ShouldCarryYouTubeSource(
        bool isBrowserSession,
        string? currentBrowserPlatformHint,
        string? lastSource,
        string? stableSource,
        bool isCompletingAdTransition)
    {
        ReadOnlySpan<char> hint = currentBrowserPlatformHint.AsSpan();
        ReadOnlySpan<char> previous = lastSource.AsSpan();
        ReadOnlySpan<char> stable = stableSource.AsSpan();

        int result = vn_browser_media_should_carry_youtube_source(
            isBrowserSession ? (byte)1 : (byte)0,
            hint, hint.Length,
            previous, previous.Length,
            stable, stable.Length,
            isCompletingAdTransition ? (byte)1 : (byte)0,
            out byte carry);
        NativeResult.RequireSuccess(result, nameof(ShouldCarryYouTubeSource));
        return carry != 0;
    }

    private static BrowserAdTransitionDecision ToDecision(VnBrowserAdTransitionDecision decision, DateTimeKind kind)
    {
        DateTime started = decision.TransitionStartedUtcTicks == 0
            ? DateTime.MinValue
            : new DateTime(decision.TransitionStartedUtcTicks, kind);
        return new BrowserAdTransitionDecision(decision.ShouldHold != 0, started, decision.WasTransitionActive != 0);
    }
}
