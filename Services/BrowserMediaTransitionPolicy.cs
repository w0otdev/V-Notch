using VNotch.Models;
using VNotch.Interop;

namespace VNotch.Services;

// Pure decisions over caller-owned snapshots. No dispatcher, I/O, clock reads or mutable state.
internal static class BrowserMediaTransitionPolicy
{
    internal static readonly TimeSpan LikelyAdQuarantineWindow = TimeSpan.FromSeconds(45);

#pragma warning disable S107
    public static BrowserAdTransitionDecision EvaluateLikelyYouTubeAd(
        bool isBrowserSession, bool hasStableTrack, string? lastSource,
        string? lastPublishedSessionInstanceKey, string? currentSessionInstanceKey,
        string? currentTrack, TimeSpan currentDuration, bool hasYouTubeWindow,
        bool currentTrackMatchesYouTubeWindow, DateTime transitionStartedUtc, DateTime nowUtc) =>
        MediaPolicyNative.EvaluateLikelyYouTubeAd(isBrowserSession, hasStableTrack, lastSource,
            lastPublishedSessionInstanceKey, currentSessionInstanceKey, currentTrack, currentDuration,
            hasYouTubeWindow, currentTrackMatchesYouTubeWindow, transitionStartedUtc, nowUtc);
#pragma warning restore S107

    public static BrowserAdTransitionDecision EvaluateYouTubeJunkMetadata(
        bool isBrowserSession, bool hasStableTrack, string? lastSource, bool hasYouTubeWindow,
        DateTime transitionStartedUtc, DateTime nowUtc) =>
        MediaPolicyNative.EvaluateYouTubeJunkMetadata(isBrowserSession, hasStableTrack, lastSource,
            hasYouTubeWindow, transitionStartedUtc, nowUtc);

    public static bool ShouldCarryYouTubeSource(bool isBrowserSession, string? currentBrowserPlatformHint,
        string? lastSource, string? stableSource, bool isCompletingAdTransition) =>
        MediaPolicyNative.ShouldCarryYouTubeSource(isBrowserSession, currentBrowserPlatformHint,
            lastSource, stableSource, isCompletingAdTransition);
}
