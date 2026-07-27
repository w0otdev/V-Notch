namespace VNotch.Services;

internal static class BrowserMediaTransitionPolicy
{
    internal static readonly TimeSpan LikelyAdMaximumDuration = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan LikelyAdQuarantineWindow = TimeSpan.FromSeconds(45);

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
        DateTime nowUtc)
    {
        bool wasTransitionActive = transitionStartedUtc != DateTime.MinValue;
        bool isRecoverableSource = MediaPlatformExtensions.ParsePlatform(lastSource) is
            MediaPlatform.Browser or
            MediaPlatform.YouTube;
        bool sessionChanged =
            !string.IsNullOrWhiteSpace(lastPublishedSessionInstanceKey) &&
            !string.IsNullOrWhiteSpace(currentSessionInstanceKey) &&
            !string.Equals(
                lastPublishedSessionInstanceKey,
                currentSessionInstanceKey,
                StringComparison.Ordinal);
        bool hasAdSizedTimeline =
            currentDuration <= TimeSpan.Zero ||
            currentDuration <= LikelyAdMaximumDuration;

        bool isCandidate =
            isBrowserSession &&
            hasStableTrack &&
            isRecoverableSource &&
            sessionChanged &&
            !string.IsNullOrWhiteSpace(currentTrack) &&
            hasYouTubeWindow &&
            !currentTrackMatchesYouTubeWindow &&
            hasAdSizedTimeline;

        if (!isCandidate)
        {
            return new BrowserAdTransitionDecision(
                ShouldHold: false,
                TransitionStartedUtc: DateTime.MinValue,
                WasTransitionActive: wasTransitionActive);
        }

        if (transitionStartedUtc == DateTime.MinValue || nowUtc < transitionStartedUtc)
        {
            transitionStartedUtc = nowUtc;
        }

        bool shouldHold =
            nowUtc - transitionStartedUtc < LikelyAdQuarantineWindow;

        return new BrowserAdTransitionDecision(
            shouldHold,
            transitionStartedUtc,
            wasTransitionActive);
    }

    public static BrowserAdTransitionDecision EvaluateYouTubeJunkMetadata(
        bool isBrowserSession,
        bool hasStableTrack,
        string? lastSource,
        bool hasYouTubeWindow,
        DateTime transitionStartedUtc,
        DateTime nowUtc)
    {
        bool wasTransitionActive = transitionStartedUtc != DateTime.MinValue;
        bool isRecoverableSource = MediaPlatformExtensions.ParsePlatform(lastSource) is
            MediaPlatform.Browser or
            MediaPlatform.YouTube;
        bool isCandidate =
            isBrowserSession &&
            hasStableTrack &&
            isRecoverableSource &&
            hasYouTubeWindow;

        if (!isCandidate)
        {
            return new BrowserAdTransitionDecision(
                ShouldHold: false,
                TransitionStartedUtc: DateTime.MinValue,
                WasTransitionActive: wasTransitionActive);
        }

        if (transitionStartedUtc == DateTime.MinValue || nowUtc < transitionStartedUtc)
        {
            transitionStartedUtc = nowUtc;
        }

        return new BrowserAdTransitionDecision(
            ShouldHold: nowUtc - transitionStartedUtc < LikelyAdQuarantineWindow,
            TransitionStartedUtc: transitionStartedUtc,
            WasTransitionActive: wasTransitionActive);
    }

    public static bool ShouldCarryYouTubeSource(
        bool isBrowserSession,
        string? currentBrowserPlatformHint,
        string? lastSource,
        string? stableSource,
        bool isCompletingAdTransition)
    {
        if (!isBrowserSession ||
            !string.Equals(
                currentBrowserPlatformHint,
                MediaPlatform.YouTube.ToDisplayString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return isCompletingAdTransition ||
               MediaPlatformExtensions.ParsePlatform(lastSource) == MediaPlatform.YouTube ||
               MediaPlatformExtensions.ParsePlatform(stableSource) == MediaPlatform.YouTube;
    }
}

internal readonly record struct BrowserAdTransitionDecision(
    bool ShouldHold,
    DateTime TransitionStartedUtc,
    bool WasTransitionActive);
