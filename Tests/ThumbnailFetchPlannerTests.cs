using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class ThumbnailFetchPlannerTests
{
    #region ClassifySources

    [Fact]
    public void Classify_YouTubePlatform_IsPotentialYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsYouTube = true,
            HasTrack = true,
        });

        Assert.True(plan.IsPotentialYouTube);
        Assert.False(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserLikelyYouTube_IsPotentialYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = true,
            HasTrack = true,
        });

        Assert.True(plan.IsPotentialYouTube);
    }

    [Fact]
    public void Classify_BrowserApp_DefaultsToYouTube_WhenNoSoundCloudOverride()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasSoundCloudSessionOverride = false,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.True(plan.IsPotentialYouTube);
        Assert.True(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserApp_WithSoundCloudOverride_NotYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasSoundCloudSessionOverride = true,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialYouTube);
        Assert.True(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserNoApp_NotForcedYouTube()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = false,
            IsLikelyYouTube = false,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialYouTube);
        Assert.True(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_SoundCloudPlatform_IsPotentialSoundCloud()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsSoundCloud = true,
            HasTrack = true,
        });

        Assert.True(plan.IsPotentialSoundCloud);
        Assert.False(plan.IsPotentialYouTube);
    }

    [Fact]
    public void Classify_BrowserYouTubeTabOpen_BlocksSoundCloudProbe()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            BrowserHasYouTubeTabOpen = true,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserSoundCloudProbe_RequiresTrack()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasTrack = false,
            ThumbnailIsNullOrPlaceholder = true,
        });

        Assert.False(plan.IsPotentialSoundCloud);
    }

    [Fact]
    public void Classify_BrowserWithGoodThumbnail_NoSoundCloudProbe()
    {
        var plan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsBrowser = true,
            HasSourceApp = true,
            IsLikelyYouTube = false,
            HasTrack = true,
            ThumbnailIsNullOrPlaceholder = false,
            HasSoundCloudSessionOverride = false,
        });

        Assert.False(plan.IsPotentialSoundCloud);
    }

    #endregion

    #region ShouldStartSoundCloudFetch

    [Fact]
    public void SoundCloudFetch_NewTrack_Starts()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_NewTrack_StartsEvenIfSameFetchRunning()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = true,
            SameTrackFetchRunning = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_SameTrackRunning_DoesNotStart()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            IsNewSoundCloudTrack = false,
            SameTrackFetchRunning = true,
            ThumbnailIsNullOrPlaceholder = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_PlaceholderRetry_StartsWhenIntervalElapsed()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            ThumbnailIsNullOrPlaceholder = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_MismatchedSourceRetry_StartsWhenIntervalElapsed()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            HasMismatchedThumbSource = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void SoundCloudFetch_RetryBlockedUntilIntervalElapses()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            ThumbnailIsNullOrPlaceholder = true,
            RetryIntervalElapsed = false,
        }));
    }

    [Fact]
    public void SoundCloudFetch_GoodThumbnail_DoesNotStart()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
        {
            ThumbnailIsNullOrPlaceholder = false,
            HasMismatchedThumbSource = false,
            RetryIntervalElapsed = true,
        }));
    }

    #endregion

    #region ShouldStartYouTubeFetch

    [Fact]
    public void YouTubeFetch_NewTrack_StartsImmediately()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
        {
            IsNewTrack = true,
            SameTrackFetchRunning = true,
        }));
    }

    [Fact]
    public void YouTubeFetch_SameTrackRunning_DoesNotRestart()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
        {
            SameTrackFetchRunning = true,
            NeedsBetterThumbnail = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void YouTubeFetch_FailedAttempt_RetriesAfterInterval()
    {
        Assert.True(ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
        {
            NeedsBetterThumbnail = true,
            RetryIntervalElapsed = true,
        }));
    }

    [Fact]
    public void YouTubeFetch_FailedAttempt_WaitsForRetryInterval()
    {
        Assert.False(ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
        {
            NeedsBetterThumbnail = true,
            RetryIntervalElapsed = false,
        }));
    }

    #endregion
}
