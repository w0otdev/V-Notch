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
    private async Task FetchSoundCloudThumbnailAsync(MediaInfo info, CancellationToken token, string trackDuringFetch, string artistDuringFetch, string sourceAppDuringFetch, string sessionKeyDuringFetch, int thumbGenAtStart, int fetchGeneration, bool requireStrongMatch, CancellationTokenSource? ownedCts = null)
    {
        int lifecycleGenAtStart = Volatile.Read(ref _lifecycleGeneration);
        bool IsCurrentSoundCloudFetch() =>
            !_disposed &&
            _state is not (ServiceLifecycleState.Stopping or ServiceLifecycleState.Disposed) &&
            lifecycleGenAtStart == Volatile.Read(ref _lifecycleGeneration) &&
            !token.IsCancellationRequested &&
            fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration) &&
            Volatile.Read(ref _thumbnailFetchGeneration) == thumbGenAtStart &&
            IsStillSamePublishedTrack(trackDuringFetch, artistDuringFetch, sourceAppDuringFetch, sessionKeyDuringFetch);

        try
        {
            if (!IsCurrentSoundCloudFetch())
                return;

            string? artworkUrl = null;
            string? browserSoundCloudUrl = TryExtractSoundCloudUrlFromAnyBrowser();
            if (!string.IsNullOrEmpty(browserSoundCloudUrl))
            {
                artworkUrl = await TryGetSoundCloudArtworkFromUrlAsync(browserSoundCloudUrl, trackDuringFetch, artistDuringFetch, token);
            }

            if ((string.IsNullOrEmpty(artworkUrl) || IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl)) &&
                IsCurrentSoundCloudFetch())
            {
                artworkUrl = await TryGetSoundCloudArtworkUrlAsync(trackDuringFetch, artistDuringFetch, requireStrongMatch, token);
            }

            if ((string.IsNullOrEmpty(artworkUrl) || IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl)) &&
                string.IsNullOrEmpty(browserSoundCloudUrl) &&
                IsCurrentSoundCloudFetch())
            {
                _windowTitleScanner.InvalidateUrlCaches();
                browserSoundCloudUrl = TryExtractSoundCloudUrlFromAnyBrowser();
                if (!string.IsNullOrEmpty(browserSoundCloudUrl))
                {
                    artworkUrl = await TryGetSoundCloudArtworkFromUrlAsync(browserSoundCloudUrl, trackDuringFetch, artistDuringFetch, token);
                }
            }

            if (string.IsNullOrEmpty(artworkUrl) ||
                IsLikelySoundCloudPlaceholderArtworkUrl(artworkUrl) ||
                !IsCurrentSoundCloudFetch())
            {
                return;
            }

            BitmapSource? frameBitmap = await DownloadImageAsync(artworkUrl, token);
            if (frameBitmap == null ||
                IsLikelySoundCloudPlaceholderThumbnail(frameBitmap) ||
                !IsCurrentSoundCloudFetch())
            {
                return;
            }

            if (info.Platform == MediaPlatform.YouTube || IsLikelyYouTube(info))
            {
                RuntimeLog.Debug(MediaThumbCropLogTag,
                    $"soundcloud-fetch suppressed: track resolved as YouTube track='{trackDuringFetch}' cachedSource='{_cachedThumbnailSource}'");
                return;
            }

            frameBitmap = CropToSquare(frameBitmap, SoundCloudPlatformName) ?? frameBitmap;

            MediaInfo? publishedSnapshot = null;
            await _updateLock.WaitAsync(token);
            try
            {
                if (!IsCurrentSoundCloudFetch() ||
                    info.Platform == MediaPlatform.YouTube ||
                    _lastPublishedInfo?.Platform == MediaPlatform.YouTube ||
                    IsLikelyYouTube(info))
                {
                    return;
                }

                _timelineSimulator.RecoveredThumbnail = frameBitmap;
                CachedThumbnail = frameBitmap;
                _cachedThumbnailSource = MediaPlatform.SoundCloud.ToDisplayString();
                if (info.Platform == MediaPlatform.Browser)
                {
                    MediaSourceClassifier.SetPlatformRunning(info, MediaPlatform.SoundCloud);
                    SetSessionSourceOverride(info, MediaPlatform.SoundCloud.ToDisplayString());

                    string currentTrackIdentity = BuildTrackIdentity(info.CurrentTrack, info.CurrentArtist);
                    string currentTrackOnlyIdentity = BuildTrackIdentity(info.CurrentTrack, "");
                    if (!string.IsNullOrEmpty(currentTrackIdentity))
                    {
                        _sourceCache.SetBoth(currentTrackIdentity, currentTrackOnlyIdentity, MediaPlatform.SoundCloud.ToDisplayString());
                        _sourceCache.QueueSave();
                    }
                }
                info.Thumbnail = frameBitmap;

                var target = _lastPublishedInfo ?? info;
                target.Thumbnail = frameBitmap;
                if (info.Platform == MediaPlatform.SoundCloud && target.Platform == MediaPlatform.Browser)
                {
                    MediaSourceClassifier.SetPlatformRunning(target, MediaPlatform.SoundCloud);
                }

                _lastPublishedInfo = target.Clone();
                string sig = _lastPublishedInfo.GetSignature();
                _lastPublishedSignature = sig;
                _lastTrackSignature = sig;
                _lastPublishedTrackIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, _lastPublishedInfo.CurrentArtist);
                _lastPublishedTrackOnlyIdentity = BuildTrackIdentity(_lastPublishedInfo.CurrentTrack, "");

                publishedSnapshot = _lastPublishedInfo.Clone();
                publishedSnapshot.IsThumbnailOnlyUpdate = true;
            }
            finally
            {
                _updateLock.Release();
            }

            if (publishedSnapshot != null)
            {
                await FireMediaChangedAsync(publishedSnapshot);
            }
        }
        catch (OperationCanceledException) { /* Cancelled */ }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SOUNDCLOUD-FETCH", ex.ToString());
        }
        finally
        {
            if (fetchGeneration == Volatile.Read(ref _soundCloudFetchGeneration))
            {
                Interlocked.Exchange(ref _soundCloudFetchInFlight, 0);
            }
            ReleaseThumbnailCts(ownedCts);
        }
    }

}
