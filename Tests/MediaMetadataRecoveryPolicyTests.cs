using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class MediaMetadataRecoveryPolicyTests
{
    [Fact]
    public void SelectDetectionMode_PlayingWithoutTitle_AwaitsMetadata()
    {
        var mode = MediaMetadataRecoveryPolicy.SelectDetectionMode(
            isAnyMediaPlaying: true,
            currentTrack: "",
            isThrottled: false);

        Assert.Equal(DetectionMode.AwaitingMetadata, mode);
    }

    [Fact]
    public void SelectDetectionMode_ActivePausedSessionWithoutTitle_AwaitsMetadata()
    {
        var mode = MediaMetadataRecoveryPolicy.SelectDetectionMode(
            isAnyMediaPlaying: true,
            currentTrack: "",
            isThrottled: false);

        Assert.Equal(DetectionMode.AwaitingMetadata, mode);
    }

    [Fact]
    public void SelectDetectionMode_NoSessionGapRecovery_RemainsIdle()
    {
        var mode = MediaMetadataRecoveryPolicy.SelectDetectionMode(
            isAnyMediaPlaying: false,
            currentTrack: "",
            isThrottled: false);

        Assert.Equal(DetectionMode.Idle, mode);
    }

    [Fact]
    public void SelectDetectionMode_TransientSessionGap_AwaitsMetadataWithoutLiveSession()
    {
        var mode = MediaMetadataRecoveryPolicy.SelectDetectionMode(
            isAnyMediaPlaying: false,
            currentTrack: "",
            isThrottled: false,
            isSessionGapRecovery: true);

        Assert.Equal(DetectionMode.AwaitingMetadata, mode);
    }

    [Theory]
    [InlineData(-1, 350)]
    [InlineData(0, 350)]
    [InlineData(1999, 350)]
    [InlineData(2000, 1000)]
    [InlineData(7999, 1000)]
    [InlineData(8000, 3000)]
    public void AwaitingMetadataPollInterval_UsesBoundedFastProbesThenBacksOff(
        int elapsedMilliseconds,
        int expectedMilliseconds)
    {
        var interval = MediaMetadataRecoveryPolicy.GetAwaitingMetadataPollInterval(
            TimeSpan.FromMilliseconds(elapsedMilliseconds));

        Assert.Equal(expectedMilliseconds, interval.TotalMilliseconds);
    }

    [Theory]
    [InlineData(false, "", true)]
    [InlineData(false, "Browser", true)]
    [InlineData(false, "YouTube", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "Browser", false)]
    public void BrowserWindowFallback_OnlyAcceptsUnresolvedNonSpotifySource(
        bool isSpotifyPlaying,
        string mediaSource,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaMetadataRecoveryPolicy.CanUseBrowserWindowTitleFallback(
                isSpotifyPlaying,
                mediaSource));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void JunkTitlePipeline_ContinuesOnlyForBrowserSessions(
        bool isJunkTitle,
        bool isBrowserSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaMetadataRecoveryPolicy.ShouldContinueAfterJunkTitle(
                isJunkTitle,
                isBrowserSession));
    }

    [Theory]
    [InlineData(true, "", true)]
    [InlineData(true, "   ", true)]
    [InlineData(true, "Real track", false)]
    [InlineData(false, "", false)]
    public void ActiveEmptyPublish_IsAlwaysSuppressed(
        bool isAnyMediaPlaying,
        string currentTrack,
        bool expected)
    {
        Assert.Equal(
            expected,
            MediaMetadataRecoveryPolicy.ShouldSuppressActiveEmptyPublish(
                isAnyMediaPlaying,
                currentTrack));
    }

    [Theory]
    [InlineData("Browser")]
    [InlineData("YouTube")]
    [InlineData("SoundCloud")]
    public void TransientSessionGap_FirstAndObservedOnePoint46SecondMiss_Hold(
        string lastSource)
    {
        DateTime now = new(2026, 7, 27, 1, 0, 0, DateTimeKind.Utc);

        var first = MediaMetadataRecoveryPolicy.EvaluateTransientSessionGap(
            hasResolvedSession: false,
            hasStableTrack: true,
            lastSource,
            gapStartedUtc: DateTime.MinValue,
            nowUtc: now);
        var repeated = MediaMetadataRecoveryPolicy.EvaluateTransientSessionGap(
            hasResolvedSession: false,
            hasStableTrack: true,
            lastSource,
            gapStartedUtc: first.GapStartedUtc,
            nowUtc: now.AddSeconds(1.46));

        Assert.True(first.ShouldHold);
        Assert.True(repeated.ShouldHold);
        Assert.Equal(now, first.GapStartedUtc);
        Assert.Equal(first.GapStartedUtc, repeated.GapStartedUtc);
    }

    [Fact]
    public void TransientSessionGap_AtGraceBoundary_ExpiresWithoutRearming()
    {
        DateTime started = new(2026, 7, 27, 1, 0, 0, DateTimeKind.Utc);

        var expired = MediaMetadataRecoveryPolicy.EvaluateTransientSessionGap(
            hasResolvedSession: false,
            hasStableTrack: true,
            lastSource: "YouTube",
            gapStartedUtc: started,
            nowUtc: started + MediaMetadataRecoveryPolicy.TransientSessionGapGrace);
        var stillExpired = MediaMetadataRecoveryPolicy.EvaluateTransientSessionGap(
            hasResolvedSession: false,
            hasStableTrack: true,
            lastSource: "YouTube",
            gapStartedUtc: expired.GapStartedUtc,
            nowUtc: started + MediaMetadataRecoveryPolicy.TransientSessionGapGrace +
                    TimeSpan.FromSeconds(1));

        Assert.False(expired.ShouldHold);
        Assert.False(stillExpired.ShouldHold);
        Assert.Equal(started, expired.GapStartedUtc);
        Assert.Equal(started, stillExpired.GapStartedUtc);
    }

    [Theory]
    [InlineData(true, true, "YouTube")]
    [InlineData(false, false, "YouTube")]
    [InlineData(false, true, "Spotify")]
    public void TransientSessionGap_IneligibleOrResolvedState_DoesNotHold(
        bool hasResolvedSession,
        bool hasStableTrack,
        string lastSource)
    {
        DateTime now = new(2026, 7, 27, 1, 0, 0, DateTimeKind.Utc);

        var result = MediaMetadataRecoveryPolicy.EvaluateTransientSessionGap(
            hasResolvedSession,
            hasStableTrack,
            lastSource,
            gapStartedUtc: now.AddSeconds(-1),
            nowUtc: now);

        Assert.False(result.ShouldHold);
        Assert.Equal(DateTime.MinValue, result.GapStartedUtc);
    }
}
