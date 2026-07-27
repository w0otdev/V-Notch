using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class BrowserMediaTransitionPolicyTests
{
    private static readonly DateTime Base =
        new(2026, 7, 27, 2, 0, 0, DateTimeKind.Utc);

    private static BrowserAdTransitionDecision Evaluate(
        string currentTrack = "Brand advertisement",
        double durationSeconds = 15,
        bool currentTrackMatchesYouTubeWindow = false,
        DateTime? transitionStartedUtc = null,
        DateTime? nowUtc = null,
        bool isBrowserSession = true,
        bool hasStableTrack = true,
        string lastSource = "YouTube",
        string lastSession = "chrome.exe|old",
        string currentSession = "chrome.exe|ad",
        bool hasYouTubeWindow = true)
        => BrowserMediaTransitionPolicy.EvaluateLikelyYouTubeAd(
            isBrowserSession,
            hasStableTrack,
            lastSource,
            lastSession,
            currentSession,
            currentTrack,
            TimeSpan.FromSeconds(durationSeconds),
            hasYouTubeWindow,
            currentTrackMatchesYouTubeWindow,
            transitionStartedUtc ?? DateTime.MinValue,
            nowUtc ?? Base);

    [Theory]
    [InlineData(5.021)]
    [InlineData(15.021)]
    [InlineData(0)]
    public void RecreatedShortMismatchedYouTubeSession_IsQuarantined(
        double durationSeconds)
    {
        var result = Evaluate(durationSeconds: durationSeconds);

        Assert.True(result.ShouldHold);
        Assert.Equal(Base, result.TransitionStartedUtc);
    }

    [Fact]
    public void ChainedSecondAd_KeepsOriginalQuarantineStart()
    {
        var first = Evaluate();
        var second = Evaluate(
            currentTrack: "Second advertiser",
            currentSession: "chrome.exe|ad-2",
            transitionStartedUtc: first.TransitionStartedUtc,
            nowUtc: Base.AddSeconds(6));

        Assert.True(second.ShouldHold);
        Assert.Equal(first.TransitionStartedUtc, second.TransitionStartedUtc);
        Assert.True(second.WasTransitionActive);
    }

    [Fact]
    public void LongRealVideoAfterSkip_ReleasesImmediatelyAndMarksRecovery()
    {
        var result = Evaluate(
            currentTrack: "Real video",
            durationSeconds: 312.241,
            transitionStartedUtc: Base,
            nowUtc: Base.AddSeconds(10),
            currentSession: "chrome.exe|content");

        Assert.False(result.ShouldHold);
        Assert.Equal(DateTime.MinValue, result.TransitionStartedUtc);
        Assert.True(result.WasTransitionActive);
    }

    [Fact]
    public void MatchingShortRealVideo_ReleasesImmediately()
    {
        var result = Evaluate(
            currentTrack: "Short real video",
            durationSeconds: 30,
            currentTrackMatchesYouTubeWindow: true,
            transitionStartedUtc: Base,
            nowUtc: Base.AddSeconds(2));

        Assert.False(result.ShouldHold);
        Assert.True(result.WasTransitionActive);
    }

    [Fact]
    public void QuarantineExpiry_DoesNotRearm()
    {
        var expired = Evaluate(
            transitionStartedUtc: Base,
            nowUtc: Base + BrowserMediaTransitionPolicy.LikelyAdQuarantineWindow);
        var stillExpired = Evaluate(
            transitionStartedUtc: expired.TransitionStartedUtc,
            nowUtc: Base + BrowserMediaTransitionPolicy.LikelyAdQuarantineWindow +
                    TimeSpan.FromSeconds(2));

        Assert.False(expired.ShouldHold);
        Assert.False(stillExpired.ShouldHold);
        Assert.Equal(Base, expired.TransitionStartedUtc);
        Assert.Equal(Base, stillExpired.TransitionStartedUtc);
    }

    [Theory]
    [InlineData(false, true, "YouTube", "chrome.exe|old", "chrome.exe|ad", true)]
    [InlineData(true, false, "YouTube", "chrome.exe|old", "chrome.exe|ad", true)]
    [InlineData(true, true, "Spotify", "chrome.exe|old", "chrome.exe|ad", true)]
    [InlineData(true, true, "YouTube", "chrome.exe|same", "chrome.exe|same", true)]
    [InlineData(true, true, "YouTube", "chrome.exe|old", "chrome.exe|ad", false)]
    public void IneligibleTransition_IsNotQuarantined(
        bool isBrowserSession,
        bool hasStableTrack,
        string lastSource,
        string lastSession,
        string currentSession,
        bool hasYouTubeWindow)
    {
        var result = Evaluate(
            isBrowserSession: isBrowserSession,
            hasStableTrack: hasStableTrack,
            lastSource: lastSource,
            lastSession: lastSession,
            currentSession: currentSession,
            hasYouTubeWindow: hasYouTubeWindow);

        Assert.False(result.ShouldHold);
    }

    [Theory]
    [InlineData("Browser", "", true, true)]
    [InlineData("Browser", "YouTube", false, true)]
    [InlineData("YouTube", "", false, true)]
    [InlineData("Browser", "", false, false)]
    [InlineData("YouTube", "", true, true)]
    public void YouTubeSourceContinuity_UsesStableOrAdTransitionEvidence(
        string lastSource,
        string stableSource,
        bool isCompletingAdTransition,
        bool expected)
    {
        bool actual = BrowserMediaTransitionPolicy.ShouldCarryYouTubeSource(
            isBrowserSession: true,
            currentBrowserPlatformHint: "YouTube",
            lastSource,
            stableSource,
            isCompletingAdTransition);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void YouTubeSourceContinuity_RequiresYouTubeWindowHint()
    {
        Assert.False(BrowserMediaTransitionPolicy.ShouldCarryYouTubeSource(
            isBrowserSession: true,
            currentBrowserPlatformHint: "SoundCloud",
            lastSource: "YouTube",
            stableSource: "YouTube",
            isCompletingAdTransition: true));
    }

    [Fact]
    public void YouTubeSourceContinuity_RequiresBrowserSession()
    {
        Assert.False(BrowserMediaTransitionPolicy.ShouldCarryYouTubeSource(
            isBrowserSession: false,
            currentBrowserPlatformHint: "YouTube",
            lastSource: "YouTube",
            stableSource: "YouTube",
            isCompletingAdTransition: true));
    }
}
