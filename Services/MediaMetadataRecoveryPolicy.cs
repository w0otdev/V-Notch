namespace VNotch.Services;

internal static class MediaMetadataRecoveryPolicy
{
    private static readonly TimeSpan FastProbeWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackoffProbeWindow = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan TransientSessionGapGrace = TimeSpan.FromSeconds(2);

    public static DetectionMode SelectDetectionMode(
        bool isAnyMediaPlaying,
        string? currentTrack,
        bool isThrottled,
        bool isSessionGapRecovery = false)
    {
        if (isSessionGapRecovery ||
            (isAnyMediaPlaying && string.IsNullOrWhiteSpace(currentTrack)))
        {
            return DetectionMode.AwaitingMetadata;
        }

        if (!isAnyMediaPlaying || string.IsNullOrWhiteSpace(currentTrack))
        {
            return DetectionMode.Idle;
        }

        return isThrottled
            ? DetectionMode.ThrottledMedia
            : DetectionMode.EventDriven;
    }

    public static TimeSpan GetAwaitingMetadataPollInterval(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < FastProbeWindow)
        {
            return TimeSpan.FromMilliseconds(350);
        }

        if (elapsed < BackoffProbeWindow)
        {
            return TimeSpan.FromSeconds(1);
        }

        return TimeSpan.FromSeconds(3);
    }

    public static bool CanUseBrowserWindowTitleFallback(bool isSpotifyPlaying, string? mediaSource)
    {
        if (isSpotifyPlaying)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(mediaSource) ||
               MediaPlatformExtensions.ParsePlatform(mediaSource) == MediaPlatform.Browser;
    }

    public static bool ShouldContinueAfterJunkTitle(bool isJunkTitle, bool isBrowserSession)
        => isJunkTitle && isBrowserSession;

    public static bool ShouldSuppressActiveEmptyPublish(
        bool isAnyMediaPlaying,
        string? currentTrack)
        => isAnyMediaPlaying && string.IsNullOrWhiteSpace(currentTrack);

    public static TransientSessionGapDecision EvaluateTransientSessionGap(
        bool hasResolvedSession,
        bool hasStableTrack,
        string? lastSource,
        DateTime gapStartedUtc,
        DateTime nowUtc)
    {
        if (hasResolvedSession ||
            !hasStableTrack ||
            !IsRecoverableSessionGapSource(lastSource))
        {
            return new TransientSessionGapDecision(false, DateTime.MinValue);
        }

        if (gapStartedUtc == DateTime.MinValue || nowUtc < gapStartedUtc)
        {
            gapStartedUtc = nowUtc;
        }

        bool shouldHold = nowUtc - gapStartedUtc < TransientSessionGapGrace;
        return new TransientSessionGapDecision(shouldHold, gapStartedUtc);
    }

    private static bool IsRecoverableSessionGapSource(string? mediaSource)
    {
        return MediaPlatformExtensions.ParsePlatform(mediaSource) is
            MediaPlatform.Browser or
            MediaPlatform.YouTube or
            MediaPlatform.SoundCloud;
    }
}

internal readonly record struct TransientSessionGapDecision(
    bool ShouldHold,
    DateTime GapStartedUtc);
