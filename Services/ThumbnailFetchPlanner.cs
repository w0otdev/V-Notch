namespace VNotch.Services;

internal readonly struct ThumbnailSourceInputs
{
    public bool PlatformIsYouTube { get; init; }
    public bool PlatformIsBrowser { get; init; }
    public bool PlatformIsSoundCloud { get; init; }
    public bool HasSourceApp { get; init; }
    public bool IsLikelyYouTube { get; init; }
    public bool HasSoundCloudSessionOverride { get; init; }
    public bool BrowserHasYouTubeTabOpen { get; init; }
    public bool HasTrack { get; init; }
    public bool ThumbnailIsNullOrPlaceholder { get; init; }
}

internal readonly struct ThumbnailSourcePlan
{
    public bool IsPotentialYouTube { get; init; }
    public bool IsPotentialSoundCloud { get; init; }
}

internal readonly struct SoundCloudFetchInputs
{
    public bool IsNewSoundCloudTrack { get; init; }
    public bool SameTrackFetchRunning { get; init; }
    public bool ThumbnailIsNullOrPlaceholder { get; init; }
    public bool HasMismatchedThumbSource { get; init; }
    public bool RetryIntervalElapsed { get; init; }
}

internal readonly struct YouTubeFetchInputs
{
    public bool IsNewTrack { get; init; }
    public bool SameTrackFetchRunning { get; init; }
    public bool NeedsBetterThumbnail { get; init; }
    public bool RetryIntervalElapsed { get; init; }
}

internal static class ThumbnailFetchPlanner
{
    public static ThumbnailSourcePlan ClassifySources(in ThumbnailSourceInputs x)
    {
        bool isPotentialYouTube = x.PlatformIsYouTube || (x.PlatformIsBrowser && x.IsLikelyYouTube);

        bool isBrowserApp = x.PlatformIsBrowser && x.HasSourceApp;
        if (isBrowserApp && !isPotentialYouTube && !x.HasSoundCloudSessionOverride)
        {
            isPotentialYouTube = true;
        }

        bool shouldProbeSoundCloudFromBrowser = x.PlatformIsBrowser &&
                                                !x.IsLikelyYouTube &&
                                                !x.BrowserHasYouTubeTabOpen &&
                                                x.HasTrack &&
                                                (x.ThumbnailIsNullOrPlaceholder || x.HasSoundCloudSessionOverride);

        bool isPotentialSoundCloud = x.PlatformIsSoundCloud || shouldProbeSoundCloudFromBrowser;

        return new ThumbnailSourcePlan
        {
            IsPotentialYouTube = isPotentialYouTube,
            IsPotentialSoundCloud = isPotentialSoundCloud,
        };
    }

    public static bool ShouldStartSoundCloudFetch(in SoundCloudFetchInputs x)
    {
        if (!x.IsNewSoundCloudTrack && x.SameTrackFetchRunning)
        {
            return false;
        }

        bool shouldRetryPlaceholder = (x.ThumbnailIsNullOrPlaceholder || x.HasMismatchedThumbSource) &&
                                      x.RetryIntervalElapsed;

        return x.IsNewSoundCloudTrack || shouldRetryPlaceholder;
    }

    public static bool ShouldStartYouTubeFetch(in YouTubeFetchInputs x)
    {
        if (!x.IsNewTrack && x.SameTrackFetchRunning)
        {
            return false;
        }

        return x.IsNewTrack || (x.NeedsBetterThumbnail && x.RetryIntervalElapsed);
    }
}
