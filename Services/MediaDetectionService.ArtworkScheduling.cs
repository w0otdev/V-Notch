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
#pragma warning disable S3776
    private void StartThumbnailFetchIfNeeded(MediaInfo info, bool isNewTrackForThumbnail)
    {
        bool forceFetchForTrackChange = isNewTrackForThumbnail;
        if (forceFetchForTrackChange)
        {
            _windowTitleScanner.InvalidateUrlCaches();
            ClearMismatchCache();
            ForgetVideoIdCacheExceptForTrack(BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist));
            _soundCloudArtworkAttemptCount = 0;
        }

        bool hasSoundCloudSessionOverride = !string.IsNullOrEmpty(info.SourceAppId) &&
                                            TryGetSessionSourceOverride(info, out var sourceOverride) &&
                                            MediaPlatformExtensions.ParsePlatform(sourceOverride) == MediaPlatform.SoundCloud;

        bool thumbnailIsNullOrPlaceholder = info.Thumbnail == null || IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail);
        bool canNeedBrowserUrlProbe = forceFetchForTrackChange ||
                                      thumbnailIsNullOrPlaceholder ||
                                      info.Thumbnail!.PixelWidth < 200;
        bool browserHasYouTubeTabOpen = false;
        if (info.Platform == MediaPlatform.Browser && !hasSoundCloudSessionOverride && canNeedBrowserUrlProbe)
        {
            try
            {
                var browserUrl = _windowTitleScanner.TryGetMediaUrlFromAnyBrowser();
                if (!string.IsNullOrEmpty(browserUrl))
                {
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
            }
            catch (Exception ex)
            {
                RuntimeLog.Debug("MEDIA-URL", $"URL scan ignored: {ex.Message}");
            }
        }
        var sourcePlan = ThumbnailFetchPlanner.ClassifySources(new ThumbnailSourceInputs
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
        bool isPotentialYouTube = sourcePlan.IsPotentialYouTube;
        bool isPotentialSoundCloud = sourcePlan.IsPotentialSoundCloud;

        string youTubeTrackIdentity = isPotentialYouTube
            ? $"{BuildTrackIdentity(info.CurrentTrack, "")}|{info.SourceAppId}|{info.SessionInstanceKey}"
            : "";
        string soundCloudTrackIdentity = isPotentialSoundCloud
            ? BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist)
            : "";

        bool needsFetch = forceFetchForTrackChange || (info.Thumbnail == null || info.Thumbnail.PixelWidth < 120);
        if (_timelineSimulator.IsThrottled && _timelineSimulator.RecoveredThumbnail != null && !forceFetchForTrackChange) needsFetch = false;

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

        if (needsFetch && info.Thumbnail != null &&
            !IsLikelySoundCloudPlaceholderThumbnail(info.Thumbnail) && (info.Thumbnail.PixelWidth >= 200 || IsLikelySoundCloudArtworkCandidate(info.Thumbnail)))
        {
            double thumbAspect = (double)info.Thumbnail.PixelWidth / info.Thumbnail.PixelHeight;
            bool isNearSquare = thumbAspect >= 0.85 && thumbAspect <= 1.15;
            bool isTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                  info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

            if (isNearSquare || isTopicChannel)
            {
                needsFetch = false;
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
            }
        }

        if (isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack) && needsFetch)
        {
            bool sameTrackFetchRunning =
                Volatile.Read(ref _youTubeFetchInFlight) == 1 &&
                string.Equals(_youTubeFetchIdentity, youTubeTrackIdentity, StringComparison.Ordinal);
            bool shouldStartYouTubeFetch = ThumbnailFetchPlanner.ShouldStartYouTubeFetch(new YouTubeFetchInputs
            {
                IsNewTrack = forceFetchForTrackChange,
                SameTrackFetchRunning = sameTrackFetchRunning,
                NeedsBetterThumbnail = needsFetch,
                RetryIntervalElapsed =
                    (DateTime.UtcNow - _lastYouTubeThumbnailAttemptTimeUtc) >= YouTubeThumbnailRetryInterval,
            });

            if (!shouldStartYouTubeFetch)
                return;

            _lastYouTubeThumbnailAttemptTimeUtc = DateTime.UtcNow;
            var cts = CreateAndReplaceThumbnailCts();
            var token = cts.Token;
            int fetchGeneration = Interlocked.Increment(ref _youTubeFetchGeneration);
            _youTubeFetchIdentity = youTubeTrackIdentity;
            Interlocked.Exchange(ref _youTubeFetchInFlight, 1);

            Interlocked.Increment(ref _soundCloudFetchGeneration);
            _soundCloudFetchIdentity = "";
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            bool shouldForceThumbFetch = forceFetchForTrackChange;

            string trackDuringFetch = info.CurrentTrack;
            string artistDuringFetch = info.CurrentArtist;
            string sourceAppDuringFetch = info.SourceAppId ?? "";
            string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
            int generationAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

            bool isBrowserIcon = info.Thumbnail != null && info.Platform == MediaPlatform.Browser;
            bool needsBetterThumb = shouldForceThumbFetch || info.Thumbnail == null || info.Thumbnail.PixelWidth < 120 || isBrowserIcon;

            _ = Task.Run(() => FetchYouTubeThumbnailAsync(
                info, token, shouldForceThumbFetch, trackDuringFetch, artistDuringFetch,
                sourceAppDuringFetch, sessionKeyDuringFetch, generationAtStart,
                fetchGeneration, needsBetterThumb, cts), token);
        }
        else if (isPotentialSoundCloud && !isPotentialYouTube && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            bool isNewSoundCloudTrack = !string.IsNullOrEmpty(soundCloudTrackIdentity) &&
                                        !string.Equals(soundCloudTrackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
            if (isNewSoundCloudTrack)
            {
                _soundCloudArtworkAttemptCount = 0;
            }

            bool sameTrackFetchRunning =
                Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
                string.Equals(_soundCloudFetchIdentity, soundCloudTrackIdentity, StringComparison.Ordinal);
            bool hasMismatchedThumbSource = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) != MediaPlatform.SoundCloud;

            bool shouldStartSoundCloudFetch =
                (thumbnailIsNullOrPlaceholder || hasMismatchedThumbSource) &&
                _soundCloudArtworkAttemptCount < MaxSoundCloudArtworkAttemptsPerTrack &&
                ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
                {
                    IsNewSoundCloudTrack = isNewSoundCloudTrack,
                    SameTrackFetchRunning = sameTrackFetchRunning,
                    ThumbnailIsNullOrPlaceholder = thumbnailIsNullOrPlaceholder,
                    HasMismatchedThumbSource = hasMismatchedThumbSource,
                    RetryIntervalElapsed = (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval,
                });

            if (shouldStartSoundCloudFetch)
            {
                _soundCloudArtworkAttemptCount++;
                _lastSoundCloudArtworkAttemptTimeUtc = DateTime.UtcNow;
                _lastSoundCloudArtworkIdentity = soundCloudTrackIdentity;
                var cts = CreateAndReplaceThumbnailCts();
                var token = cts.Token;
                int fetchGeneration = Interlocked.Increment(ref _soundCloudFetchGeneration);
                _soundCloudFetchIdentity = soundCloudTrackIdentity;
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);

                Interlocked.Increment(ref _youTubeFetchGeneration);
                _youTubeFetchIdentity = "";
                Interlocked.Exchange(ref _youTubeFetchInFlight, 0);

                string trackDuringFetch = info.CurrentTrack;
                string artistDuringFetch = info.CurrentArtist;
                string sourceAppDuringFetch = info.SourceAppId ?? "";
                string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
                int thumbGenAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

                bool requireStrongMatch = info.Platform != MediaPlatform.SoundCloud;

                _ = Task.Run(() => FetchSoundCloudThumbnailAsync(info, token, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch, thumbGenAtStart, fetchGeneration, requireStrongMatch, cts), token);
            }
        }
        else if (info.Platform == MediaPlatform.Spotify && !string.IsNullOrEmpty(info.CurrentTrack))
        {
            string spotifyTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
            bool isNewSpotifyTrack = !string.IsNullOrEmpty(spotifyTrackIdentity) &&
                                     !string.Equals(spotifyTrackIdentity, _lastSoundCloudArtworkIdentity, StringComparison.Ordinal);
            if (isNewSpotifyTrack)
            {
                _soundCloudArtworkAttemptCount = 0;
            }

            bool sameTrackFetchRunning =
                Volatile.Read(ref _soundCloudFetchInFlight) == 1 &&
                string.Equals(_soundCloudFetchIdentity, spotifyTrackIdentity, StringComparison.Ordinal);
            bool hasMismatchedThumbSource = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) != MediaPlatform.Spotify;

            bool shouldStartSpotifyFetch =
                (thumbnailIsNullOrPlaceholder || hasMismatchedThumbSource) &&
                _soundCloudArtworkAttemptCount < MaxSoundCloudArtworkAttemptsPerTrack &&
                ThumbnailFetchPlanner.ShouldStartSoundCloudFetch(new SoundCloudFetchInputs
                {
                    IsNewSoundCloudTrack = isNewSpotifyTrack,
                    SameTrackFetchRunning = sameTrackFetchRunning,
                    ThumbnailIsNullOrPlaceholder = thumbnailIsNullOrPlaceholder,
                    HasMismatchedThumbSource = hasMismatchedThumbSource,
                    RetryIntervalElapsed = (DateTime.UtcNow - _lastSoundCloudArtworkAttemptTimeUtc) >= SoundCloudArtworkRetryInterval,
                });

            if (shouldStartSpotifyFetch)
            {
                _soundCloudArtworkAttemptCount++;
                _lastSoundCloudArtworkAttemptTimeUtc = DateTime.UtcNow;
                _lastSoundCloudArtworkIdentity = spotifyTrackIdentity;
                var cts = CreateAndReplaceThumbnailCts();
                var token = cts.Token;
                int fetchGeneration = Interlocked.Increment(ref _soundCloudFetchGeneration);
                _soundCloudFetchIdentity = spotifyTrackIdentity;
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);

                Interlocked.Increment(ref _youTubeFetchGeneration);
                _youTubeFetchIdentity = "";
                Interlocked.Exchange(ref _youTubeFetchInFlight, 0);

                string trackDuringFetch = info.CurrentTrack;
                string artistDuringFetch = info.CurrentArtist;
                string sourceAppDuringFetch = info.SourceAppId ?? "";
                string sessionKeyDuringFetch = info.SessionInstanceKey ?? "";
                int thumbGenAtStart = Volatile.Read(ref _thumbnailFetchGeneration);

                _ = Task.Run(() => FetchSpotifyThumbnailAsync(info, token, trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch, thumbGenAtStart, fetchGeneration, cts), token);
            }
        }
        else
        {
            CancelAndClearThumbnailCts();
            Interlocked.Increment(ref _youTubeFetchGeneration);
            _youTubeFetchIdentity = "";
            Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            Interlocked.Increment(ref _soundCloudFetchGeneration);
            _soundCloudFetchIdentity = "";
            Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
        }
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
