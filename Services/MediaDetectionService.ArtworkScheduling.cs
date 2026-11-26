using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed partial class MediaDetectionService : IMediaDetectionService, IAsyncDisposable
{
    internal Task StartThumbnailFetchIfNeeded(MediaInfo info, bool isNewTrackForThumbnail)
    {
        if (isNewTrackForThumbnail)
        {
            _windowTitleScanner.InvalidateUrlCaches();
            ClearMismatchCache();
            ForgetVideoIdCacheExceptForTrack(BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
            _soundCloudArtworkAttemptCount = 0;
        }

        bool thumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail);
        var sourcePlan = ClassifyArtworkSources(info, isNewTrackForThumbnail, thumbnailIsNullOrPlaceholder);
        bool needsFetch = ResolveNeedsArtworkFetch(info, isNewTrackForThumbnail, sourcePlan.IsPotentialYouTube, sourcePlan.IsPotentialSoundCloud);

        if (sourcePlan.IsPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack) && needsFetch)
        {
            string youTubeTrackIdentity = $"{BuildTrackIdentity(info.CurrentTrack, "")}|{info.SourceAppId}|{info.SessionInstanceKey}";
            return TryScheduleYouTubeArtworkFetch(info, isNewTrackForThumbnail, youTubeTrackIdentity, needsFetch);
        }

        if (sourcePlan.IsPotentialSoundCloud && !sourcePlan.IsPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            bool requireStrongMatch = info.Platform != MediaPlatform.SoundCloud;
            return TryScheduleTrackArtworkFetch(
                info, MediaPlatform.SoundCloud, trackIdentity, thumbnailIsNullOrPlaceholder,
                (token, track, artist, sourceApp, sessionKey, thumbGen, fetchGen, cts) =>
                    FetchSoundCloudThumbnailAsync(info, token, track, artist, sourceApp, sessionKey, thumbGen, fetchGen, requireStrongMatch, cts));
        }

        if (info.Platform == MediaPlatform.Spotify && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            string trackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            return TryScheduleTrackArtworkFetch(
                info, MediaPlatform.Spotify, trackIdentity, thumbnailIsNullOrPlaceholder,
                (token, track, artist, sourceApp, sessionKey, thumbGen, fetchGen, cts) =>
                    FetchSpotifyThumbnailAsync(info, token, track, artist, sourceApp, sessionKey, thumbGen, fetchGen, cts));
        }

        CancelAndResetThumbnailFetches();
        return Task.CompletedTask;
    }

    private ThumbnailSourcePlan ClassifyArtworkSources(MediaInfo info, bool forceFetchForTrackChange, bool thumbnailIsNullOrPlaceholder)
    {
        bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                            TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                            MediaPlatformExtensions.ParsePlatform(sourceOverride) == MediaPlatform.SoundCloud;

        bool canNeedBrowserUrlProbe = forceFetchForTrackChange ||
                                      thumbnailIsNullOrPlaceholder ||
                                      info.Thumbnail!.PixelWidth < 200;
        bool browserHasYouTubeTabOpen = false;
        if (info.Platform == MediaPlatform.Browser && !hasSoundCloudSessionOverride && canNeedBrowserUrlProbe)
        {
            ProbeBrowserMediaSource(ref browserHasYouTubeTabOpen, ref hasSoundCloudSessionOverride);
        }

        return ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
        {
            PlatformIsYouTube = info.Platform == MediaPlatform.YouTube,
            PlatformIsBrowser = info.Platform == MediaPlatform.Browser,
            PlatformIsSoundCloud = info.Platform == MediaPlatform.SoundCloud,
            HasSourceApp = !string.IsNullOrEmpty(info.SourceAppId),
            IsLikelyYouTube = IsLikelyYouTube(info),
            HasSoundCloudSessionOverride = hasSoundCloudSessionOverride,
            BrowserHasYouTubeTabOpen = browserHasYouTubeTabOpen,
            HasTrack = !string.IsNullOrEmpty(info.CurrentTrack),
            ThumbnailIsNullOrPlaceholder = thumbnailIsNullOrPlaceholder,
        });
    }

    private void ProbeBrowserMediaSource(ref bool browserHasYouTubeTabOpen, ref bool hasSoundCloudSessionOverride)
    {
        try
        {
            var browserUrl = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
            if (string.IsNullOrEmpty(browserUrl)) return;

            if (browserUrl.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                browserUrl.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
            {
                browserHasYouTubeTabOpen = true;
            }
            else if (!string.IsNullOrEmpty(SoundCloudMatching.ExtractTrackUrl(browserUrl)))
            {
                hasSoundCloudSessionOverride = true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("MEDIA-URL", $"URL scan ignored: {ex.Message}");
        }
    }

    private bool ResolveNeedsArtworkFetch(
        MediaInfo info,
        bool forceFetchForTrackChange,
        bool isPotentialYouTube,
        bool isPotentialSoundCloud)
    {
        bool needsFetch = forceFetchForTrackChange || (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
        if (_timelineSimulator.IsThrottled && _timelineSimulator.RecoveredThumbnail != null && !forceFetchForTrackChange)
            needsFetch = false;

        // The player's artwork identifies the playing upload; a title search can
        // return a different recording or music video with the same song name.
        if (isPotentialYouTube && info.Thumbnail != null &&
            _lastSessionArtworkFingerprint.HasValue &&
            _sessionArtworkGeneration == Volatile.Read(ref _thumbnailFetchGeneration))
        {
            needsFetch = false;
        }

        if (needsFetch && !forceFetchForTrackChange && CachedThumbnail != null && CachedThumbnail.PixelWidth >= 200)
        {
            needsFetch = false;
            info.Thumbnail = CachedThumbnail;
            _timelineSimulator.RecoveredThumbnail = CachedThumbnail;
        }

        if (!needsFetch || info.Thumbnail == null ||
            IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail) ||
            (info.Thumbnail.PixelWidth < 200 && !IsLikelySoundCloudArtworkCandidate(info.Thumbnail)))
        {
            return needsFetch;
        }

        double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
        bool isNearSquare = thumbAspect >= 0.85 && thumbAspect <= 1.15;
        bool isTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                              info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

        if (!isNearSquare && !isTopicChannel) return true;

        string thumbnailSource = isPotentialSoundCloud && !isPotentialYouTube
            ? SoundCloudPlatformName
            : info.MediaSource ?? YouTubePlatformName;
        if (!ReferenceEquals(info.Thumbnail, _lastInitialCroppedThumbnail))
        {
            RuntimeLog.Debug(MediaThumbCropLogTag,
                $"path=initial-smtc track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' thumb={info.Thumbnail.PixelWidth}x{info.Thumbnail.PixelHeight} aspect={thumbAspect:F2} isNearSquare={isNearSquare} isTopicChannel={isTopicChannel}");
            var cropped = CropToSquare(info.Thumbnail, thumbnailSource, forceCenterCrop: isTopicChannel) ?? info.Thumbnail;
            info.Thumbnail = cropped;
            CachedThumbnail = cropped;
            _timelineSimulator.RecoveredThumbnail = cropped;
        }
        _cachedThumbnailSource = isPotentialYouTube ? YouTubePlatformName : thumbnailSource;
        return false;
    }

    private Task TryScheduleYouTubeArtworkFetch(
        MediaInfo info,
        bool forceFetchForTrackChange,
        string youTubeTrackIdentity,
        bool needsFetch)
    {
        bool sameTrackFetchRunning =
            Volatile.Read(ref _youTubeFetchInFlight) == 1 &&
            string.Equals(_youTubeFetchIdentity, youTubeTrackIdentity, StringComparison.Ordinal);
        bool shouldStart = ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
        {
            IsNewTrack = forceFetchForTrackChange,
            SameTrackFetchRunning = sameTrackFetchRunning,
            NeedsBetterThumbnail = needsFetch,
            RetryIntervalElapsed =
                (DateTime.UtcNow - _lastYouTubeThumbnailAttemptTimeUtc) >= YouTubeThumbnailRetryInterval,
        });
        if (!shouldStart) return Task.CompletedTask;

        _lastYouTubeThumbnailAttemptTimeUtc = DateTime.UtcNow;
        var cts = CreateAndReplaceThumbnailCts();
        var token = cts.Token;
        int fetchGeneration = Interlocked.Increment(ref _youTubeFetchGeneration);
        _youTubeFetchIdentity = youTubeTrackIdentity;
        Interlocked.Exchange(ref _youTubeFetchInFlight, 1);

        Interlocked.Increment(ref _soundCloudFetchGeneration);
        _soundCloudFetchIdentity = "";
        Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);

        string trackDuringFetch = info.CurrentTrack;
        string artistDuringFetch = info.CurrentArtist;
        string sourceAppDuringFetch = info.SourceAppId ?? "";
        string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
        int generationAtStart = Volatile.Read(ref _thumbnailFetchGeneration);
        bool isBrowserIcon = info.Thumbnail != null && info.Platform == MediaPlatform.Browser;
        bool needsBetterThumb = forceFetchForTrackChange || info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

        return Task.Run(() => FetchYouTubeThumbnailAsync(
            info, token, forceFetchForTrackChange, trackDuringFetch, artistDuringFetch,
            sourceAppDuringFetch, sessionKeyDuringFetch, generationAtStart,
            fetchGeneration, needsBetterThumb, cts), token);
    }

    private Task TryScheduleTrackArtworkFetch(
        MediaInfo info,
        MediaPlatform expectedPlatform,
        string trackIdentity,
        bool thumbnailIsNullOrPlaceholder,
        Func<CancellationToken, string, string, string, string, int, int, CancellationTokenSource, Task> fetchAsync)
    {
        bool isNewTrack = !string.IsNullOrEmpty(trackIdentity) &&
                          !string.Equals(trackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
        if (isNewTrack)
            _soundCloudArtworkAttemptCount = 0;

        bool sameTrackFetchRunning =
            Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
            string.Equals(_soundCloudFetchIdentity, trackIdentity, StringComparison.Ordinal);
        bool hasMismatchedThumbSource = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) != expectedPlatform;

        bool shouldStart =
            (thumbnailIsNullOrPlaceholder || hasMismatchedThumbSource) &&
            _soundCloudArtworkAttemptCount < MaxSoundCloudArtworkAttemptsPerTrack &&
            ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
            {
                IsNewSoundCloudTrack = isNewTrack,
                SameTrackFetchRunning = sameTrackFetchRunning,
                ThumbnailIsNullOrPlaceholder = thumbnailIsNullOrPlaceholder,
                HasMismatchedThumbSource = hasMismatchedThumbSource,
                RetryIntervalElapsed = (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval,
            });
        if (!shouldStart) return Task.CompletedTask;

        _soundCloudArtworkAttemptCount++;
        _lastSoundCloudArtworkAttemptTimeUtc = DateTime.UtcNow;
        _lastSoundCloudArtworkIdentity = trackIdentity;
        var cts = CreateAndReplaceThumbnailCts();
        var token = cts.Token;
        int fetchGeneration = Interlocked.Increment(ref _soundCloudFetchGeneration);
        _soundCloudFetchIdentity = trackIdentity;
        Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);

        Interlocked.Increment(ref _youTubeFetchGeneration);
        _youTubeFetchIdentity = "";
        Interlocked.Exchange(ref _youTubeFetchInFlight, 0);

        string trackDuringFetch = info.CurrentTrack;
        string artistDuringFetch = info.CurrentArtist;
        string sourceAppDuringFetch = info.SourceAppId ?? "";
        string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
        int thumbGenAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

        return Task.Run(() => fetchAsync(
            token, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch,
            sessionKeyDuringFetch, thumbGenAtStart, fetchGeneration, cts), token);
    }

    private void CancelAndResetThumbnailFetches()
    {
        CancelAndClearThumbnailCts();
        Interlocked.Increment(ref _youTubeFetchGeneration);
        _youTubeFetchIdentity = "";
        Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
        Interlocked.Increment(ref _soundCloudFetchGeneration);
        _soundCloudFetchIdentity = "";
        Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
    }

    private CancellationTokenSource CreateAndReplaceThumbnailCts()
    {
        CancellationTokenSource cts;
        var bgCts = _bgCts;
        if (bgCts != null)
        {
            try
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(bgCts.Token);
            }
            catch (ObjectDisposedException)
            {
                cts = new CancellationTokenSource();
            }
        }
        else
        {
            cts = new CancellationTokenSource();
        }

        var oldCts = Interlocked.Exchange(ref _thumbCts, cts);
        CancelAndDisposeThumbnailCts(oldCts);
        return cts;
    }

    private void CancelAndClearThumbnailCts()
    {
        var oldCts = Interlocked.Exchange(ref _thumbCts, null);
        CancelAndDisposeThumbnailCts(oldCts);
    }

    private void ReleaseThumbnailCts(CancellationTokenSource? ownedCts)
    {
        if (ownedCts == null) return;
        Interlocked.CompareExchange(ref _thumbCts, null, ownedCts);
        try
        {
            ownedCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CancelAndDisposeThumbnailCts(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

}
