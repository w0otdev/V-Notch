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
#pragma warning disable S107
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
            var fetch = new YouTubeArtworkFetch(this, info, token, shouldForceThumbFetch, trackDuringFetch, artistDuringFetch,
                sourceAppDuringFetch, sessionKeyDuringFetch, generationAtStart, fetchGeneration, needsBetterThumb);
            await fetch.RunAsync();
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

    private sealed class YouTubeArtworkFetch(MediaDetectionService owner, MediaInfo info, CancellationToken token,
        bool shouldForceThumbFetch, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch,
        string sessionKeyDuringFetch, int generationAtStart, int fetchGeneration, bool needsBetterThumb)
    {
        private readonly int lifecycleGenAtStart = Volatile.Read(ref owner._lifecycleGeneration);
        private readonly string trackArtistIdentity = BuildTrackIdentity(trackDuringFetch, artistDuringFetch);
        private readonly string youtubeSourceDisplay = MediaPlatform.YouTube.ToDisplayString();
        private string? videoId;
        private string? preferredThumbnailUrl;
        private bool titleSearchAttempted;
        private bool timelineRecovered;

        private bool MatchesFetchedTrack() =>
            owner.IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch) ||
            owner.IsStillSamePublishedTrack(trackDuringFetch, info.CurrentArtist, sourceAppDuringFetch, sessionKeyDuringFetch);

        private bool IsCurrentFetch() =>
            !owner._disposed &&
            owner._state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
            lifecycleGenAtStart == Volatile.Read(ref owner._lifecycleGeneration) &&
            !token.IsCancellationRequested &&
            info.Platform != MediaPlatform.SoundCloud &&
            owner._lastPublishedInfo?.Platform != MediaPlatform.SoundCloud &&
            (owner._lastPublishedInfo == null || owner._lastPublishedInfo.IsIndeterminate == info.IsIndeterminate) &&
            generationAtStart == Volatile.Read(ref owner._thumbnailFetchGeneration) &&
            fetchGeneration == Volatile.Read(ref owner._youTubeFetchGeneration) &&
            MatchesFetchedTrack();

        private async Task<bool> ApplyIfCurrentAsync(Action apply)
        {
            await owner._updateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsCurrentFetch())
                    return false;

                apply();
                return true;
            }
            finally
            {
                owner._updateLock.Release();
            }
        }

        private void RecoverDuration(TimeSpan duration)
        {
            if (info.IsIndeterminate || !MediaTimelinePolicy.HasFiniteDuration(duration))
                return;

            owner._timelineSimulator.RecoveredDuration = duration;
            info.Duration = duration;
            info.IsTimelinePending = false;
            timelineRecovered = true;
        }

        private void ApplyLookupMetadata(
            YouTubeLookupResult lookup,
            string? cacheVideoId,
            bool confirmYouTubeSource,
            bool resetCompetingRunningFlags,
            bool requireDataApiForThumbnail)
        {
            if (cacheVideoId != null)
            {
                owner.CacheVideoIdForTrack(trackArtistIdentity, cacheVideoId);
                owner.CacheVideoIdForTrack(trackDuringFetch, cacheVideoId);
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
                owner.SetSessionSourceOverride(info, youtubeSourceDisplay);
                owner._sourceCache.SetBoth(trackDuringFetch, trackArtistIdentity, youtubeSourceDisplay);
                owner._sourceCache.QueueSave();
            }

            if (!string.IsNullOrEmpty(lookup.Author) && lookup.Author != YouTubePlatformName)
            {
                info.CurrentArtist = lookup.Author;
                owner._stableArtist = lookup.Author;
                owner._lastSourceConfirmedTime = DateTime.UtcNow;
            }

            RecoverDuration(lookup.Duration);

            if ((!requireDataApiForThumbnail || lookup.Source == YouTubeLookupSource.DataApi) &&
                !string.IsNullOrWhiteSpace(lookup.ThumbnailUrl))
            {
                preferredThumbnailUrl = lookup.ThumbnailUrl;
            }
        }

        private void SyncYouTubeFieldsOnto(MediaInfo target, string resolvedVideoId)
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

        private MediaInfo RecordAndClonePublishedSnapshot(MediaInfo target)
        {
            owner._lastPublishedInfo = target.Clone();
            string sig = owner._lastPublishedInfo.GetSignature();
            owner._lastPublishedSignature = sig;
            owner._lastTrackSignature = sig;
            owner._lastPublishedTrackIdentity = BuildTrackIdentity(owner._lastPublishedInfo.CurrentTrack, owner._lastPublishedInfo.CurrentArtist);
            owner._lastPublishedTrackOnlyIdentity = BuildTrackIdentity(owner._lastPublishedInfo.CurrentTrack, "");
            return owner._lastPublishedInfo.Clone();
        }

        private async Task<bool> TryResolveByTitleSearchAsync(bool resetCompetingFlags, string debugTag)
        {
            titleSearchAttempted = true;
            var searchResult = await owner._metadataLookup.TrySearchYouTubeByTitleAsync(trackDuringFetch, artistDuringFetch, token);
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

        private async Task<(BitmapSource? Frame, bool Aborted)> TryDownloadBestYouTubeFrameAsync(string currentVideoId, string? preferredUrl)
        {
            if (!string.IsNullOrWhiteSpace(preferredUrl))
            {
                var preferred = await owner.DownloadImageAsync(preferredUrl, token);
                if (!IsCurrentFetch()) return (null, true);
                if (preferred is { PixelWidth: >= 200 }) return (preferred, false);
            }

            for (int i = 0; i < YouTubeThumbnailFallbacks.Length; i++)
            {
                var (variant, minWidth) = YouTubeThumbnailFallbacks[i];
                var candidate = await owner.DownloadImageAsync($"https://i.ytimg.com/vi/{currentVideoId}/{variant}", token);
                if (!IsCurrentFetch()) return (null, true);
                if (candidate != null && candidate.PixelWidth >= minWidth)
                    return (candidate, false);
            }

            return (null, false);
        }

        private async Task<bool> ValidateInitialVideoAsync()
        {
            videoId = string.IsNullOrEmpty(videoId) ? owner.TryExtractVideoIdFromAnyBrowserUrl() : videoId;
            videoId = string.IsNullOrEmpty(videoId) ? owner.TryExtractVideoIdFromBrowserUrl() : videoId;
            videoId = string.IsNullOrEmpty(videoId) ? owner.GetCachedVideoIdForTrack(trackDuringFetch) : videoId;

            if (!string.IsNullOrEmpty(videoId) && owner.TryGetCachedMismatchVideoId(videoId))
                videoId = null;

            if (string.IsNullOrEmpty(videoId) || videoId == info.YouTubeVideoId) return true;
            string ytUrl = $"https://www.youtube.com/watch?v={videoId}";
            var urlResult = await owner._metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(ytUrl, token);
            if (!IsCurrentFetch())
                return false;

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
                    owner.CacheMismatchVideoId(mismatchedId);
                    owner.EvictVideoIdCacheEntry(trackDuringFetch, mismatchedId);
                }))
                {
                    return false;
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
                    return false;
                }
            }
            return true;
        }

        private async Task<bool> ResolveVideoAsync()
        {
            if (string.IsNullOrEmpty(videoId))
            {
                var result = await owner.TryGetYouTubeVideoIdWithInfoAsync(trackDuringFetch, token);
                if (!IsCurrentFetch())
                    return false;

                if (result != null)
                {
                    videoId = result.Id;
                    bool highConfidence = result.TitleMatches(trackDuringFetch) || info.Platform == MediaPlatform.YouTube;
                    if (!await ApplyIfCurrentAsync(() =>
                        ApplyLookupMetadata(result, cacheVideoId: null, confirmYouTubeSource: highConfidence,
                            resetCompetingRunningFlags: false, requireDataApiForThumbnail: true)))
                    {
                        return false;
                    }
                }
            }

            if (string.IsNullOrEmpty(videoId) && !titleSearchAttempted &&
                !await TryResolveByTitleSearchAsync(resetCompetingFlags: false, "title-search-resolved"))
            {
                return false;
            }
            return true;
        }

        private async Task<bool> PublishMetadataAsync(string activeVideoId)
        {
            MediaInfo? metadataSnapshot = null;
            if (!await ApplyIfCurrentAsync(() =>
            {
                info.YouTubeVideoId = activeVideoId;
                bool shouldPublishMetadata = timelineRecovered ||
                    (!string.IsNullOrEmpty(owner._stableArtist) && owner._stableArtist != YouTubePlatformName && MatchesFetchedTrack());

                if (owner._lastPublishedInfo != null)
                    SyncYouTubeFieldsOnto(owner._lastPublishedInfo, activeVideoId);

                if (shouldPublishMetadata)
                    metadataSnapshot = RecordAndClonePublishedSnapshot(owner._lastPublishedInfo ?? info);
            }))
            {
                return false;
            }

            if (metadataSnapshot != null)
                await owner.FireMediaChangedAsync(metadataSnapshot);
            return true;
        }

        private async Task PublishFrameAsync(string activeVideoId, BitmapSource frameBitmap)
        {
            bool isYtFetchTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                         info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
            RuntimeLog.Debug(MediaThumbCropLogTag,
                $"path=youtube-fetch track='{info.CurrentTrack}' artist='{info.CurrentArtist}' thumb={frameBitmap.PixelWidth}x{frameBitmap.PixelHeight} isTopicChannel={isYtFetchTopicChannel}");
            frameBitmap = owner.CropToSquare(frameBitmap, YouTubePlatformName, forceCenterCrop: isYtFetchTopicChannel) ?? frameBitmap;
            MediaInfo? publishedSnapshot = null;
            if (!await ApplyIfCurrentAsync(() =>
            {
                owner._timelineSimulator.RecoveredThumbnail = frameBitmap;
                owner.CachedThumbnail = frameBitmap;
                owner._cachedThumbnailSource = youtubeSourceDisplay;
                info.Thumbnail = frameBitmap;

                var target = owner._lastPublishedInfo ?? info;
                target.Thumbnail = frameBitmap;
                SyncYouTubeFieldsOnto(target, activeVideoId);
                publishedSnapshot = RecordAndClonePublishedSnapshot(target);
                publishedSnapshot.IsThumbnailOnlyUpdate = true;
            }))
            {
                return;
            }

            if (publishedSnapshot != null)
                await owner.FireMediaChangedAsync(publishedSnapshot);
        }

        private async Task<bool> ShouldRetryArtworkAsync(string activeVideoId)
        {
            if (!await PublishMetadataAsync(activeVideoId)) return false;
            if (!needsBetterThumb && info.Platform != MediaPlatform.YouTube) return false;
            var (frame, aborted) = await TryDownloadBestYouTubeFrameAsync(activeVideoId, preferredThumbnailUrl);
            if (aborted) return false;
            if (frame == null || !IsCurrentFetch()) return true;
            await PublishFrameAsync(activeVideoId, frame);
            return false;
        }

        private async Task<bool> PollForVideoAsync()
        {
            const int pollIntervalMs = 200;
            const int maxPolls = 5;

            for (int poll = 0; poll < maxPolls && !token.IsCancellationRequested; poll++)
            {
                await Task.Delay(pollIntervalMs, token);
                if (!IsCurrentFetch())
                    return false;

                owner._windowTitleScanner.InvalidateUrlCaches();

                string? polledId = owner.TryExtractVideoIdFromAnyBrowserUrl();
                if (string.IsNullOrEmpty(polledId))
                    polledId = owner.TryExtractVideoIdFromBrowserUrl();
                if (string.IsNullOrEmpty(polledId) || owner.TryGetCachedMismatchVideoId(polledId))
                    continue;

                var pollResult = await owner._metadataLookup.TryGetYouTubeVideoInfoFromUrlAsync(
                    $"https://www.youtube.com/watch?v={polledId}", token);
                if (!IsCurrentFetch())
                    return false;
                if (pollResult == null)
                    continue;

                if (!pollResult.TitleMatches(trackDuringFetch))
                {
                    owner.CacheMismatchVideoId(polledId);
                    continue;
                }

                videoId = polledId;
                return await ApplyIfCurrentAsync(() =>
                    ApplyLookupMetadata(pollResult, cacheVideoId: null, confirmYouTubeSource: true,
                        resetCompetingRunningFlags: false, requireDataApiForThumbnail: true));
            }
            return false;
        }

        private async Task<bool> PrepareRetryAsync(int retryCount)
        {
            if (videoId != null)
            {
                await Task.Delay(retryCount * 350, token);
                if (!IsCurrentFetch())
                    return false;

                videoId = null;
                preferredThumbnailUrl = null;
                // The next iteration retries a track lookup. A raw browser URL
                // here would bypass validation and could belong to another tab.
                return true;
            }
            if (await PollForVideoAsync()) return true;
            if (!IsCurrentFetch()) return false;
            videoId = null;
            preferredThumbnailUrl = null;

            if (!titleSearchAttempted && IsCurrentFetch() &&
                !await TryResolveByTitleSearchAsync(resetCompetingFlags: true, "title-search-after-poll"))
            {
                return false;
            }

            if (string.IsNullOrEmpty(videoId) && info.Platform == MediaPlatform.Browser && IsCurrentFetch())
            {
                int scGen = Interlocked.Increment(ref owner._soundCloudFetchGeneration);
                Interlocked.Exchange(ref owner._soundCloudFetchInFlight, 1);
                await owner.FetchSoundCloudThumbnailAsync(
                    info, token, trackDuringFetch, artistDuringFetch,
                    sourceAppDuringFetch, sessionKeyDuringFetch,
                    generationAtStart, scGen, requireStrongMatch: true);
                return false;
            }
            return true;
        }

        internal async Task RunAsync()
        {
            if (!IsCurrentFetch()) return;
            videoId = shouldForceThumbFetch ? null : info.YouTubeVideoId;
            if (!await ValidateInitialVideoAsync()) return;
            for (int attempt = 1; attempt <= 3 && !token.IsCancellationRequested; attempt++)
            {
                if (!await ResolveVideoAsync()) return;
                if (!string.IsNullOrEmpty(videoId) && !token.IsCancellationRequested && !await ShouldRetryArtworkAsync(videoId)) return;
                if (attempt < 3 && IsCurrentFetch() && !await PrepareRetryAsync(attempt)) return;
            }
        }
    }
}
