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
#pragma warning disable S107, S3776
    private static readonly (string Variant, int MinWidth)[] YouTubeThumbnailFallbacks =
    [
        ("maxresdefault.jpg", 400),
        ("sddefault.jpg", 400),
        ("hqdefault.jpg", 0),
        ("mqdefault.jpg", 0),
    ];

    private async Task FetchYouTubeThumbnailAsync(
        MediaInfo info,
        CancellationToken token,
        bool shouldForceThumbFetch,
        string trackDuringFetch,
        string artistDuringFetch,
        string sourceAppDuringFetch,
        string sessionKeyDuringFetch,
        int generationAtStart,
        int fetchGeneration,
        bool needsBetterThumb,
        CancellationTokenSource? ownedCts = null)
    {
        try
        {
            // Accept the artist this lookup resolved as well as the original SMTC
            // artist, while retaining the track, session and generation checks.
            int lifecycleGenAtStart = Volatile.Read(ref _lifecycleGeneration);
            string trackArtistIdentity = BuildTrackIdentity(trackDuringFetch, artistDuringFetch);
            string youtubeSourceDisplay = MediaPlatform.YouTube.ToDisplayString();

            bool MatchesFetchedTrack() =>
                IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch) ||
                IsStillSamePublishedTrack(trackDuringFetch, info.CurrentArtist, sourceAppDuringFetch, sessionKeyDuringFetch);

            bool IsCurrentFetch() =>
                !_disposed &&
                _state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
                lifecycleGenAtStart == Volatile.Read(ref _lifecycleGeneration) &&
                !token.IsCancellationRequested &&
                info.Platform != MediaPlatform.SoundCloud &&
                _lastPublishedInfo?.Platform != MediaPlatform.SoundCloud &&
                (_lastPublishedInfo == null || _lastPublishedInfo.IsIndeterminate == info.IsIndeterminate) &&
                generationAtStart == Volatile.Read(ref _thumbnailFetchGeneration) &&
                fetchGeneration == Volatile.Read(ref _youTubeFetchGeneration) &&
                MatchesFetchedTrack();

            async Task<bool> ApplyIfCurrentAsync(Action apply)
            {
                await _updateLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!IsCurrentFetch())
                        return false;

                    apply();
                    return true;
                }
                finally
                {
                    _updateLock.Release();
                }
            }

            if (!IsCurrentFetch())
                return;

            string? videoId = shouldForceThumbFetch ? null : info.YouTubeVideoId;
            string? preferredThumbnailUrl = null;
            int retryCount = 0;
            bool titleSearchAttempted = false;
            bool timelineRecovered = false;

            void RecoverDuration(TimeSpan duration)
            {
                if (info.IsIndeterminate || !MediaTimelinePolicy.HasFiniteDuration(duration))
                    return;

                _timelineSimulator.RecoveredDuration = duration;
                info.Duration = duration;
                info.IsTimelinePending = false;
                timelineRecovered = true;
            }

            void ApplyLookupMetadata(
                YouTubeLookupResult lookup,
                string? cacheVideoId,
                bool confirmYouTubeSource,
                bool resetCompetingRunningFlags,
                bool requireDataApiForThumbnail)
            {
                if (cacheVideoId != null)
                {
                    CacheVideoIdForTrack(trackArtistIdentity, cacheVideoId);
                    CacheVideoIdForTrack(trackDuringFetch, cacheVideoId);
                }

                if (confirmYouTubeSource)
                {
                    if (resetCompetingRunningFlags)
                    {
                        MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.YouTube);
                    }
                    else
                    {
                        info.MediaSource = youtubeSourceDisplay;
                        info.IsYouTubeRunning = true;
                    }
                    SetSessionSourceOverride(info, youtubeSourceDisplay);
                    _sourceCache.SetBoth(trackDuringFetch, trackArtistIdentity, youtubeSourceDisplay);
                    _sourceCache.QueueSave();
                }

                if (!string.IsNullOrEmpty(lookup.Author) && lookup.Author != YouTubePlatformName)
                {
                    info.CurrentArtist = lookup.Author;
                    _stableArtist = lookup.Author;
                    _lastSourceConfirmedTime = DateTime.UtcNow;
                }

                RecoverDuration(lookup.Duration);

                if ((!requireDataApiForThumbnail || lookup.Source == YouTubeLookupSource.DataApi) &&
                    !string.IsNullOrWhiteSpace(lookup.ThumbnailUrl))
                {
                    preferredThumbnailUrl = lookup.ThumbnailUrl;
                }
            }

            void SyncYouTubeFieldsOnto(MediaInfo target, string resolvedVideoId)
            {
                target.YouTubeVideoId = resolvedVideoId;
                if (info.Platform == MediaPlatform.YouTube)
                    MediaSourceClassifier.SetPlatformRunning(target, MediaPlatform.YouTube);
                if (!string.IsNullOrEmpty(info.CurrentArtist) && info.CurrentArtist != YouTubePlatformName)
                    target.CurrentArtist = info.CurrentArtist;
                if (timelineRecovered && MediaTimelinePolicy.HasFiniteDuration(info.Duration) && !target.IsIndeterminate)
                {
                    target.Duration = info.Duration;
                    target.IsTimelinePending = false;
                }
            }

            MediaInfo RecordAndClonePublishedSnapshot(MediaInfo target)
            {
                _lastPublishedInfo = target.Clone();
                string sig = _lastPublishedInfo.GetSignature();
                _lastPublishedSignature = sig;
                _lastTrackSignature = sig;
                _lastPublishedTrackIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, _lastPublishedInfo.CurrentArtist);
                _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, "");
                return _lastPublishedInfo.Clone();
            }

            async Task<bool> TryResolveByTitleSearchAsync(bool resetCompetingFlags, string debugTag)
            {
                titleSearchAttempted = true;
                var searchResult = await _metadataLookup.TrySearchYouTubeByTitleAsync(trackDuringFetch, artistDuringFetch, token);
                if (!IsCurrentFetch())
                    return false;

                if (searchResult != null && !string.IsNullOrEmpty(searchResult.Id))
                {
                    videoId = searchResult.Id;
                    if (!await ApplyIfCurrentAsync(() =>
                        ApplyLookupMetadata(searchResult, videoId, confirmYouTubeSource: true,
                            resetCompetingRunningFlags: resetCompetingFlags, requireDataApiForThumbnail: false)))
                    {
                        return false;
                    }

                    RuntimeLog.Debug("MEDIA-YOUTUBE-FETCH",
                        $"{debugTag} track='{trackDuringFetch}' artist='{artistDuringFetch}' videoId={videoId}");
                }
                return true;
            }

            async Task<(BitmapSource? Frame, bool Aborted)> TryDownloadBestYouTubeFrameAsync(string currentVideoId, string? preferredUrl)
            {
                if (!string.IsNullOrWhiteSpace(preferredUrl))
                {
                    var preferred = await DownloadImageAsync(preferredUrl, token);
                    if (!IsCurrentFetch()) return (null, true);
                    if (preferred is { PixelWidth: >= 200 }) return (preferred, false);
                }

                for (int i = 0; i < YouTubeThumbnailFallbacks.Length; i++)
                {
                    var (variant, minWidth) = YouTubeThumbnailFallbacks[i];
                    var candidate = await DownloadImageAsync($"https://i.ytimg.com/vi/{currentVideoId}/{variant}", token);
                    if (!IsCurrentFetch()) return (null, true);
                    if (candidate != null && candidate.PixelWidth >= minWidth)
                        return (candidate, false);
                }

                return (null, false);
            }

            videoId = string.IsNullOrEmpty(videoId) ? TryExtractVideoIdFromAnyBrowserUrl() : videoId;
            videoId = string.IsNullOrEmpty(videoId) ? TryExtractVideoIdFromBrowserUrl() : videoId;
            videoId = string.IsNullOrEmpty(videoId) ? GetCachedVideoIdForTrack(trackDuringFetch) : videoId;

            if (!string.IsNullOrEmpty(videoId) && TryGetCachedMismatchVideoId(videoId))
                videoId = null;

            if (!string.IsNullOrEmpty(videoId) && videoId != info.YouTubeVideoId)
            {
                string ytUrl = $"https://www.youtube.com/watch?v={videoId}";
                var urlResult = await _metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(ytUrl, token);
                if (!IsCurrentFetch())
                    return;

                if (urlResult == null)
                {
                    RuntimeLog.Debug("META-YOUTUBE-API",
                        $"validation-failed: oEmbed returned null for videoId={videoId} track='{trackDuringFetch}' -> discarding unvalidated videoId");
                    videoId = null;
                }
                else if (!urlResult.TitleMatches(trackDuringFetch))
                {
                    // Multiple tabs can contain different videos by the same artist.
                    // An author match alone does not identify the playing video.
                    RuntimeLog.Debug("META-YOUTUBE-API",
                        $"video-mismatch: api-title='{urlResult.Title}' smtc-track='{trackDuringFetch}' videoId={videoId} -> discarding stale videoId");
                    string mismatchedId = videoId;
                    if (!await ApplyIfCurrentAsync(() =>
                    {
                        CacheMismatchVideoId(mismatchedId);
                        EvictVideoIdCacheEntry(trackDuringFetch, mismatchedId);
                    }))
                    {
                        return;
                    }
                    videoId = null;
                }
                else
                {
                    string validatedId = videoId;
                    if (!await ApplyIfCurrentAsync(() =>
                        ApplyLookupMetadata(urlResult, validatedId, confirmYouTubeSource: true,
                            resetCompetingRunningFlags: false, requireDataApiForThumbnail: true)))
                    {
                        return;
                    }
                }
            }

            while (retryCount < 3 && !token.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(videoId))
                {
                    var result = await TryGetYouTubeVideoIdWithInfoAsync(trackDuringFetch, artistDuringFetch, token);
                    if (!IsCurrentFetch())
                        return;

                    if (result != null)
                    {
                        videoId = result.Id;
                        bool highConfidence = result.TitleMatches(trackDuringFetch) || info.Platform == MediaPlatform.YouTube;
                        if (!await ApplyIfCurrentAsync(() =>
                            ApplyLookupMetadata(result, cacheVideoId: null, confirmYouTubeSource: highConfidence,
                                resetCompetingRunningFlags: false, requireDataApiForThumbnail: true)))
                        {
                            return;
                        }
                    }
                }

                if (string.IsNullOrEmpty(videoId) && !titleSearchAttempted &&
                    !await TryResolveByTitleSearchAsync(resetCompetingFlags: false, "title-search-resolved"))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested)
                {
                    string activeVideoId = videoId;
                    MediaInfo? metadataSnapshot = null;
                    if (!await ApplyIfCurrentAsync(() =>
                    {
                        info.YouTubeVideoId = activeVideoId;
                        bool shouldPublishMetadata = timelineRecovered ||
                            (!string.IsNullOrEmpty(_stableArtist) && _stableArtist != YouTubePlatformName && MatchesFetchedTrack());

                        if (_lastPublishedInfo != null)
                            SyncYouTubeFieldsOnto(_lastPublishedInfo, activeVideoId);

                        if (shouldPublishMetadata)
                            metadataSnapshot = RecordAndClonePublishedSnapshot(_lastPublishedInfo ?? info);
                    }))
                    {
                        return;
                    }

                    if (metadataSnapshot != null)
                        await FireMediaChangedAsync(metadataSnapshot);

                    if (!needsBetterThumb && info.Platform != MediaPlatform.YouTube)
                        break;

                    var (frameBitmap, aborted) = await TryDownloadBestYouTubeFrameAsync(activeVideoId, preferredThumbnailUrl);
                    if (aborted)
                        return;

                    if (frameBitmap != null && IsCurrentFetch())
                    {
                        bool isYtFetchTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                                     info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                        RuntimeLog.Debug(MediaThumbCropLogTag,
                            $"path=youtube-fetch track='{info.CurrentTrack}' artist='{info.CurrentArtist}' thumb={frameBitmap.PixelWidth}x{frameBitmap.PixelHeight} isTopicChannel={isYtFetchTopicChannel}");
                        frameBitmap = CropToSquare(frameBitmap, YouTubePlatformName, forceCenterCrop: isYtFetchTopicChannel) ?? frameBitmap;
                        MediaInfo? publishedSnapshot = null;
                        if (!await ApplyIfCurrentAsync(() =>
                        {
                            _timelineSimulator.RecoveredThumbnail = frameBitmap;
                            CachedThumbnail = frameBitmap;
                            _cachedThumbnailSource = youtubeSourceDisplay;
                            info.Thumbnail = frameBitmap;

                            var target = _lastPublishedInfo ?? info;
                            target.Thumbnail = frameBitmap;
                            SyncYouTubeFieldsOnto(target, activeVideoId);
                            publishedSnapshot = RecordAndClonePublishedSnapshot(target);
                            publishedSnapshot.IsThumbnailOnlyUpdate = true;
                        }))
                        {
                            return;
                        }

                        if (publishedSnapshot != null)
                            await FireMediaChangedAsync(publishedSnapshot);
                        break;
                    }
                }

                retryCount++;
                if (retryCount >= 3 || !IsCurrentFetch())
                    continue;

                if (videoId != null)
                {
                    await Task.Delay(retryCount * 350, token);
                    if (!IsCurrentFetch())
                        return;

                    videoId = null;
                    preferredThumbnailUrl = null;
                    // The next iteration retries a track lookup. A raw browser URL
                    // here would bypass validation and could belong to another tab.
                    continue;
                }

                const int pollIntervalMs = 200;
                const int maxPolls = 5;
                bool resolved = false;

                for (int poll = 0; poll < maxPolls && !token.IsCancellationRequested; poll++)
                {
                    await Task.Delay(pollIntervalMs, token);
                    if (!IsCurrentFetch())
                        return;

                    _windowTitleScanner.InvalidateUrlCaches();

                    string? polledId = TryExtractVideoIdFromAnyBrowserUrl();
                    if (string.IsNullOrEmpty(polledId))
                        polledId = TryExtractVideoIdFromBrowserUrl();
                    if (string.IsNullOrEmpty(polledId) || TryGetCachedMismatchVideoId(polledId))
                        continue;

                    var pollResult = await _metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(
                        $"https://www.youtube.com/watch?v={polledId}", token);
                    if (!IsCurrentFetch())
                        return;
                    if (pollResult == null)
                        continue;

                    if (!pollResult.TitleMatches(trackDuringFetch))
                    {
                        CacheMismatchVideoId(polledId);
                        continue;
                    }

                    videoId = polledId;
                    if (!await ApplyIfCurrentAsync(() =>
                        ApplyLookupMetadata(pollResult, cacheVideoId: null, confirmYouTubeSource: true,
                            resetCompetingRunningFlags: false, requireDataApiForThumbnail: true)))
                    {
                        return;
                    }
                    resolved = true;
                    break;
                }

                if (resolved)
                    continue;

                videoId = null;
                preferredThumbnailUrl = null;

                if (!titleSearchAttempted && IsCurrentFetch() &&
                    !await TryResolveByTitleSearchAsync(resetCompetingFlags: true, "title-search-after-poll"))
                {
                    return;
                }

                if (string.IsNullOrEmpty(videoId) && info.Platform == MediaPlatform.Browser && IsCurrentFetch())
                {
                    int scGen = Interlocked.Increment(ref _soundCloudFetchGeneration);
                    Interlocked.Exchange(ref _soundCloudFetchInFlight, 1);
                    await FetchSoundCloudThumbnailAsync(
                        info, token, trackDuringFetch, artistDuringFetch,
                        sourceAppDuringFetch, sessionKeyDuringFetch,
                        generationAtStart, scGen, requireStrongMatch: true);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* Cancelled */ }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-YOUTUBE-FETCH", ex.ToString());
        }
        finally
        {
            if (fetchGeneration == Volatile.Read(ref _youTubeFetchGeneration))
            {
                Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
            }
            ReleaseThumbnailCts(ownedCts);
        }
    }

}
