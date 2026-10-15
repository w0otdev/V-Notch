using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaTimelineClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GrowingDvr_ConfirmsAfterTwoUpdates_AndSolverPreservesLiveEdge()
    {
        var classifier = new MediaTimelineClassifier();
        Assert.False(Observe(classifier, 7200, 0));
        Assert.False(Observe(classifier, 7201, 1));
        Assert.True(Observe(classifier, 7202, 2));

        var solved = TimelinePositionSolver.Solve(new TimelineSolveInputs
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(7202),
            MaxSeekTime = TimeSpan.FromSeconds(7202),
            Position = TimeSpan.FromSeconds(7202),
            IsLiveTimeline = true,
            IsNewTrack = true,
            IsBrowserTimelineTrack = true,
            IsPlaying = true,
            PlaybackRate = 1,
            LastUpdatedUtc = Now,
            NowUtc = Now.AddSeconds(2)
        });
        Assert.True(solved.IsIndeterminate);
        Assert.Equal(TimeSpan.FromSeconds(7202), solved.Position);
    }

    [Fact]
    public void VodAtEnd_WithFixedDuration_DoesNotBecomeLive()
    {
        var classifier = new MediaTimelineClassifier();
        for (int seconds = 0; seconds < 30; seconds++)
            Assert.False(Observe(classifier, 7200, seconds));
    }

    [Fact]
    public void SingleMetadataCorrection_DoesNotBecomeLive()
    {
        var classifier = new MediaTimelineClassifier();
        Assert.False(Observe(classifier, 7200, 0));
        Assert.False(Observe(classifier, 7201, 1));
        Assert.False(Observe(classifier, 7201, 20));
    }

    [Fact]
    public void RapidDurationCorrections_DoNotBecomeLive()
    {
        var classifier = new MediaTimelineClassifier();
        Assert.False(Observe(classifier, 7200, 0));
        Assert.False(Observe(classifier, 7300, 1));
        Assert.False(Observe(classifier, 7400, 2));
    }

    [Theory]
    [InlineData("chrome|other-tab|Broadcast", 7203)]
    [InlineData("chrome|tab|Other broadcast", 7203)]
    [InlineData("chrome|tab|Broadcast", 180)]
    public void SessionTrackOrDurationReset_DiscardsPriorLiveClassification(string identity, double endpoint)
    {
        var classifier = ConfirmLive();
        Assert.False(classifier.Observe(identity, TimeSpan.FromSeconds(endpoint), TimeSpan.Zero, Now.AddSeconds(3), true));
    }

    [Fact]
    public void SlidingSeekWindow_RecognizesDvrImmediately()
    {
        var classifier = new MediaTimelineClassifier();
        Assert.True(classifier.Observe("chrome|tab|Broadcast", TimeSpan.FromHours(2), TimeSpan.FromMinutes(30), Now, true));
    }

    [Fact]
    public void PausedDvr_KeepsClassification_AndCanResumeAfterLongPause()
    {
        var classifier = ConfirmLive();
        Assert.True(Observe(classifier, 7202, 30, playing: false));
        Assert.True(Observe(classifier, 7202, 300, playing: false));
        Assert.True(Observe(classifier, 7202, 301));
        Assert.True(Observe(classifier, 7203, 302));
    }

    [Fact]
    public void FinishedBroadcast_BecomesFiniteWhenEndpointStopsGrowing()
    {
        var classifier = ConfirmLive();
        Assert.False(Observe(classifier, 7202, 20));
    }

    [Fact]
    public void PollingBetweenTimelineUpdates_DoesNotHideEndpointGrowth()
    {
        var classifier = new MediaTimelineClassifier();
        Assert.False(Observe(classifier, 7200, 0));
        Assert.False(Observe(classifier, 7200, .5));
        Assert.False(Observe(classifier, 7201, 1));
        Assert.False(Observe(classifier, 7201, 1.5));
        Assert.True(Observe(classifier, 7202, 2));
    }

    private static MediaTimelineClassifier ConfirmLive()
    {
        var classifier = new MediaTimelineClassifier();
        Observe(classifier, 7200, 0);
        Observe(classifier, 7201, 1);
        Assert.True(Observe(classifier, 7202, 2));
        return classifier;
    }

    private static bool Observe(MediaTimelineClassifier classifier, double endpoint, double seconds, bool playing = true) =>
        classifier.Observe("chrome|tab|Broadcast", TimeSpan.FromSeconds(endpoint), TimeSpan.Zero, Now.AddSeconds(seconds), playing);
}
