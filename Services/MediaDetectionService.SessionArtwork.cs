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
    private async Task ApplySessionThumbnailWithRefreshAsync(
        MediaInfo info,
        GlobalSystemMediaTransportControlsSession? session,
        GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties,
        bool trackChangedForThisPass,
        bool forceRefresh = false)
    {
        bool browserSessionChanged =
            IsBrowserSourceApp(info.SourceAppId) &&
            !string.IsNullOrEmpty(_lastPublishedSessionInstanceKey) &&
            !string.IsNullOrEmpty(info.SessionInstanceKey) &&
            !string.Equals(_lastPublishedSessionInstanceKey, info.SessionInstanceKey, StringComparison.Ordinal);
        bool crossProviderCacheMismatch =
            info.Platform == MediaPlatform.SoundCloud &&
            MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube;
        bool hasVerifiedTrackArtwork =
            (_lastSessionArtworkFingerprint.HasValue &&
             _sessionArtworkGeneration == Volatile.Read(ref _thumbnailFetchGeneration)) ||
            !string.IsNullOrEmpty(_cachedThumbnailSource);
        if (!trackChangedForThisPass &&
            !forceRefresh &&
            !browserSessionChanged &&
            !crossProviderCacheMismatch &&
            CachedThumbnail != null &&
            hasVerifiedTrackArtwork &&
            _lastMetadataChangeTime != DateTime.MinValue &&
            (DateTime.UtcNow - _lastMetadataChangeTime) >= TimeSpan.FromSeconds(6))
        {
            info.Thumbnail = CachedThumbnail;
            return;
        }

        await ApplySessionThumbnailAsync(info, mediaProperties, trackChangedForThisPass);

        bool refreshSessionArtwork = IsBrowserSourceApp(info.SourceAppId) ||
                                     info.Platform is MediaPlatform.YouTube or MediaPlatform.SoundCloud or MediaPlatform.Browser;
        if (info.Thumbnail != null || !refreshSessionArtwork || session == null)
            return;

        try
        {
            var latestProps = await session.TryGetMediaPropertiesAsync();
            if (latestProps?.Thumbnail != null && !ReferenceEquals(latestProps, mediaProperties))
            {
                await ApplySessionThumbnailAsync(info, latestProps, false);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("MEDIA-THUMB-PROCESS", $"Deferred session thumbnail refresh skipped: {ex.Message}");
        }

        if (info.Thumbnail == null)
        {
            ScheduleDeferredSessionThumbnailRefreshIfNeeded();
        }
    }

    private bool ScheduleDeferredSessionThumbnailRefreshIfNeeded()
    {
        if (_state != ServiceLifecycleState.Running || _bgCts is not { IsCancellationRequested: false } cts)
            return false;

        int expectedGen = Volatile.Read(ref _thumbnailFetchGeneration);
        if (Interlocked.Exchange(ref _deferredThumbRefreshGeneration, expectedGen) == expectedGen)
            return false;

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (int delayMs in new[] { 250, 750 })
                {
                    await Task.Delay(delayMs, token);
                    if (expectedGen != Volatile.Read(ref _thumbnailFetchGeneration) || CachedThumbnail != null)
                        return;
                    _changes.Enqueue(ChangeTypes.Heartbeat);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on shutdown/cancellation.
            }
        }, token);
        return true;
    }

    private Task ApplySessionThumbnailAsync(MediaInfo info, GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties, bool trackChangedForThisPass)
        => ApplySessionArtworkAsync(info, trackChangedForThisPass, async () =>
        {
            var thumbnail = mediaProperties?.Thumbnail;
            if (thumbnail == null) return null;
            using var stream = await thumbnail.OpenReadAsync();
            return stream != null && stream.Size > 0
                ? await ConvertToWpfBitmapAsync(stream, _bgCts?.Token ?? CancellationToken.None)
                : null;
        });

    private async Task ApplySessionArtworkAsync(MediaInfo info, bool trackChangedForThisPass, Func<Task<BitmapSource?>> readArtwork)
    {
        int generation = Volatile.Read(ref _thumbnailFetchGeneration);
        if (info.Platform == MediaPlatform.SoundCloud &&
            MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube)
        {
            // Source resolution can arrive after a speculative browser lookup.
            // The previous provider's cache must not hide the real session artwork.
            CachedThumbnail = null;
            _cachedThumbnailSource = "";
            _timelineSimulator.RecoveredThumbnail = null;
            info.Thumbnail = null;
        }

        bool isYouTubeLikeSource = info.Platform == MediaPlatform.YouTube || (info.Platform == MediaPlatform.Browser && IsLikelyYouTube(info));
        bool browserSessionChanged =
            IsBrowserSourceApp(info.SourceAppId) &&
            !string.IsNullOrEmpty(_lastPublishedSessionInstanceKey) &&
            !string.IsNullOrEmpty(info.SessionInstanceKey) &&
            !string.Equals(
                _lastPublishedSessionInstanceKey,
                info.SessionInstanceKey,
                StringComparison.Ordinal);
        bool hasVerifiedYouTubeThumb = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.YouTube;
        bool refreshSessionArtwork = IsBrowserSourceApp(info.SourceAppId) ||
                                     info.Platform is MediaPlatform.YouTube or MediaPlatform.SoundCloud or MediaPlatform.Browser;
        info.Thumbnail = CachedThumbnail;
        if (!trackChangedForThisPass && CachedThumbnail != null && !refreshSessionArtwork)
        {
            info.Thumbnail = CachedThumbnail;
        }
        else
        {
            try
            {
                BitmapSource? newBitmap = await readArtwork();
                if (newBitmap == null || generation != Volatile.Read(ref _thumbnailFetchGeneration)) return;
                var fingerprint = ArtworkFingerprint.Create(newBitmap);
                if ((refreshSessionArtwork || (isYouTubeLikeSource && trackChangedForThisPass)) &&
                    _lastSessionArtworkFingerprint is { } previous && previous.Equals(fingerprint) &&
                    (_sessionArtworkGeneration != generation || CachedThumbnail != null))
                {
                    // Browsers can publish the new title before replacing the old artwork.
                    // Keep reading subsequent snapshots; never attach that old image to this track.
                    // A verified network lookup can still supply a legitimately shared album cover.
                    return;
                }
                bool isSoundCloudSource = info.Platform == MediaPlatform.SoundCloud;
                bool hasVerifiedSoundCloudThumb = MediaPlatformExtensions.ParsePlatform(_cachedThumbnailSource) == MediaPlatform.SoundCloud;
                bool likelySoundCloudArtwork = IsLikelySoundCloudArtworkCandidate(newBitmap);

                var thumbDecision = ThumbnailHeuristics.DecideSmtcThumbnail(new ThumbnailHeuristics.SmtcThumbnailInputs
                {
                    IsYouTubeLikeSource = isYouTubeLikeSource,
                    IsSoundCloudSource = isSoundCloudSource,
                    IsBrowserOrYouTubePlatform = info.Platform == MediaPlatform.YouTube || info.Platform == MediaPlatform.Browser,
                    IsBrowserSession = info.Platform == MediaPlatform.Browser || IsBrowserSourceApp(info.SourceAppId),
                    TrackChanged = trackChangedForThisPass,
                    BrowserSessionChanged = browserSessionChanged,
                    HasVerifiedYouTubeThumb = hasVerifiedYouTubeThumb,
                    HasVerifiedSoundCloudThumb = hasVerifiedSoundCloudThumb,
                    LikelySoundCloudArtwork = likelySoundCloudArtwork,
                    RecentTrackChange = (DateTime.UtcNow - _lastMetadataChangeTime).TotalSeconds < 4.0,
                    CachedThumbnailIsNull = CachedThumbnail == null,
                    PixelWidth = newBitmap.PixelWidth,
                    PixelHeight = newBitmap.PixelHeight,
                });

                // Keep checking the session after a track transition. Once its
                // old fingerprint is excluded, real artwork takes precedence
                // over a lookup that only matched the song title.
                bool isSessionVideoArtwork = !trackChangedForThisPass &&
                                             newBitmap.PixelWidth >= 120 && newBitmap.PixelHeight >= 90 &&
                                             (double)newBitmap.PixelWidth / newBitmap.PixelHeight > 1.3 &&
                                             !ThumbnailHeuristics.HasLowEntropyMonochromeProfile(newBitmap);
                if (isYouTubeLikeSource && !browserSessionChanged &&
                    (likelySoundCloudArtwork || isSessionVideoArtwork))
                {
                    thumbDecision = ThumbnailHeuristics.SmtcThumbnailDecision.Accept;
                }

                if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Reject)
                {
                    RuntimeLog.Debug("MEDIA-THUMB-STALE",
                        $"Rejecting stale SMTC thumbnail on track change: track='{info.CurrentTrack}' source='{info.MediaSource}' thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2}");
                    info.Thumbnail = null;
                }
                else if (thumbDecision == ThumbnailHeuristics.SmtcThumbnailDecision.Accept)
                {
                    _lastSessionArtworkFingerprint = fingerprint;
                    _sessionArtworkGeneration = generation;
                    if (refreshSessionArtwork)
                    {
                        // A pending fallback must not overwrite newer artwork from the player.
                        Interlocked.Increment(ref _youTubeFetchGeneration);
                        Interlocked.Exchange(ref _youTubeFetchInFlight, 0);
                        Interlocked.Increment(ref _soundCloudFetchGeneration);
                        Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
                        _timelineSimulator.RecoveredThumbnail = null;
                    }
                    bool isSmtcTopicChannel = !string.IsNullOrEmpty(info.CurrentArtist) &&
                                              info.CurrentArtist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
                    RuntimeLog.Debug(MediaThumbCropLogTag,
                        $"path=smtc-update track='{info.CurrentTrack}' artist='{info.CurrentArtist}' source='{info.MediaSource}' thumb={newBitmap.PixelWidth}x{newBitmap.PixelHeight} aspect={(double)newBitmap.PixelWidth / newBitmap.PixelHeight:F2} isSmtcTopicChannel={isSmtcTopicChannel}");
                    newBitmap = CropToSquare(newBitmap, info.MediaSource, forceCenterCrop: isSmtcTopicChannel) ?? newBitmap;
                    CachedThumbnail = newBitmap;
                    if (info.Platform == MediaPlatform.SoundCloud)
                    {
                        if (likelySoundCloudArtwork)
                        {
                            _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
                        }
                    }
                    else
                    {
                        _cachedThumbnailSource = info.MediaSource ?? "";
                    }
                    info.Thumbnail = CachedThumbnail;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("MEDIA-THUMB-PROCESS", ex.ToString());
            }
        }
    }

}
